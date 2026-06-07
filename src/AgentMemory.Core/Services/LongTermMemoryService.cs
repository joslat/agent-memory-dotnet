using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Core.Services;

/// <summary>
/// Service for long-term (structured knowledge) memory operations.
/// </summary>
public sealed class LongTermMemoryService : ILongTermMemoryService
{
    private readonly IEntityRepository _entityRepo;
    private readonly IFactRepository _factRepo;
    private readonly IPreferenceRepository _prefRepo;
    private readonly IRelationshipRepository _relRepo;
    private readonly IEmbeddingOrchestrator _embeddingOrchestrator;
    private readonly LongTermMemoryOptions _options;
    private readonly ILogger<LongTermMemoryService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LongTermMemoryService"/> class.
    /// </summary>
    public LongTermMemoryService(
        IEntityRepository entityRepo,
        IFactRepository factRepo,
        IPreferenceRepository prefRepo,
        IRelationshipRepository relRepo,
        IEmbeddingOrchestrator embeddingOrchestrator,
        IOptions<LongTermMemoryOptions> options,
        ILogger<LongTermMemoryService> logger)
    {
        ArgumentNullException.ThrowIfNull(entityRepo);
        ArgumentNullException.ThrowIfNull(factRepo);
        ArgumentNullException.ThrowIfNull(prefRepo);
        ArgumentNullException.ThrowIfNull(relRepo);
        ArgumentNullException.ThrowIfNull(embeddingOrchestrator);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _entityRepo = entityRepo;
        _factRepo = factRepo;
        _prefRepo = prefRepo;
        _relRepo = relRepo;
        _embeddingOrchestrator = embeddingOrchestrator;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task<Entity?> RecordEntityFeedbackAsync(
        string entityId,
        bool positive,
        double? delta = null,
        AgentMemory.Abstractions.Options.MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entityId))
            throw new ArgumentException("Entity id must be provided.", nameof(entityId));

        var magnitude = Math.Abs(delta ?? _options.FeedbackConfidenceDelta);
        var signed = positive ? magnitude : -magnitude;

        _logger.LogDebug(
            "Recording {Kind} feedback ({Delta}) for entity {EntityId}, owner={Owner}",
            positive ? "positive" : "negative", signed, entityId, scope?.OwnerId);
        return _entityRepo.ApplyConfidenceDeltaAsync(entityId, signed, scope, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<Entity> AddEntityAsync(
        Entity entity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return EnsureEmbeddingThenUpsertAsync(
            entity,
            shouldEmbed: _options.GenerateEntityEmbeddings && entity.Embedding is null,
            embed: ct =>
            {
                var text = string.IsNullOrEmpty(entity.Description) ? entity.Name : $"{entity.Name}: {entity.Description}";
                _logger.LogDebug("Generating embedding for entity {EntityId}", entity.EntityId);
                return _embeddingOrchestrator.EmbedTextAsync(text, ct);
            },
            withEmbedding: (e, emb) => e with { Embedding = emb },
            upsert: _entityRepo.UpsertAsync,
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Entity>> GetEntitiesByNameAsync(
        string name,
        bool includeAliases = true,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        return _entityRepo.GetByNameAsync(name, includeAliases, scope, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Entity>> SearchEntitiesAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        var scored = await _entityRepo.SearchByVectorAsync(queryEmbedding, limit, minScore, scope, cancellationToken);
        return scored.Select(r => r.Entity).ToList();
    }

    /// <inheritdoc/>
    public async Task<Preference> AddPreferenceAsync(
        Preference preference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preference);

        var embedding = preference.Embedding;
        if (_options.GeneratePreferenceEmbeddings && embedding is null)
        {
            _logger.LogDebug("Generating embedding for preference {PreferenceId}", preference.PreferenceId);
            embedding = await _embeddingOrchestrator.EmbedPreferenceAsync(preference.PreferenceText, cancellationToken);
        }

        // Dedup-on-create: reinforce an existing same-category, same-owner near-duplicate instead of
        // creating a new node (preferences MERGE on id, so every add would otherwise be a fresh node).
        if (_options.DeduplicateOnCreate && embedding is not null)
        {
            var dup = await _prefRepo.FindDuplicateAsync(
                preference.Category, embedding, preference.OwnerId,
                _options.DeduplicationSimilarityThreshold, cancellationToken);
            if (dup is not null)
            {
                var reinforced = BumpConfidence(dup.Confidence, preference.Confidence);
                _logger.LogDebug("Deduplicated preference in '{Category}' onto {Id} (confidence→{C}).",
                    preference.Category, dup.PreferenceId, reinforced);
                return await _prefRepo.MarkDeduplicatedAsync(dup.PreferenceId, reinforced, cancellationToken);
            }
        }

        var toSave = embedding is null ? preference : preference with { Embedding = embedding };
        return await _prefRepo.UpsertAsync(toSave, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Preference>> GetPreferencesByCategoryAsync(
        string category,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        return _prefRepo.GetByCategoryAsync(category, scope, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Preference>> SearchPreferencesAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        var scored = await _prefRepo.SearchByVectorAsync(queryEmbedding, limit, minScore, scope, cancellationToken);
        return scored.Select(r => r.Preference).ToList();
    }

    /// <inheritdoc/>
    public async Task<Fact> AddFactAsync(
        Fact fact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fact);

        var embedding = fact.Embedding;
        if (_options.GenerateFactEmbeddings && embedding is null)
        {
            _logger.LogDebug("Generating embedding for fact {FactId}", fact.FactId);
            embedding = await _embeddingOrchestrator.EmbedFactAsync(fact.Subject, fact.Predicate, fact.Object, cancellationToken);
        }

        // Dedup-on-create: reinforce an existing same-subject+predicate, same-owner near-duplicate
        // (e.g. the same fact phrased differently across sessions) instead of creating a new node.
        // Exact triples already collapse via the owner_key MERGE; this catches the near-duplicate case.
        if (_options.DeduplicateOnCreate && embedding is not null)
        {
            var dup = await _factRepo.FindDuplicateAsync(
                fact.Subject, fact.Predicate, embedding, fact.OwnerId,
                _options.DeduplicationSimilarityThreshold, cancellationToken);
            if (dup is not null)
            {
                var reinforced = BumpConfidence(dup.Confidence, fact.Confidence);
                _logger.LogDebug("Deduplicated fact '{S} {P}' onto {Id} (confidence→{C}).",
                    fact.Subject, fact.Predicate, dup.FactId, reinforced);
                return await _factRepo.MarkDeduplicatedAsync(dup.FactId, reinforced, cancellationToken);
            }
        }

        var toSave = embedding is null ? fact : fact with { Embedding = embedding };
        return await _factRepo.UpsertAsync(toSave, cancellationToken);
    }

    /// <summary>Reinforced confidence on a dedup hit: max(existing, incoming) + configured bump, capped at 1.0.</summary>
    private double BumpConfidence(double existing, double incoming) =>
        Math.Min(1.0, Math.Max(existing, incoming) + _options.DeduplicationConfidenceBump);

    /// <summary>
    /// Shared helper implementing the "generate an embedding when missing, then upsert" pattern
    /// used by <see cref="AddEntityAsync"/>, <see cref="AddPreferenceAsync"/> and <see cref="AddFactAsync"/>.
    /// </summary>
    private static async Task<T> EnsureEmbeddingThenUpsertAsync<T>(
        T item,
        bool shouldEmbed,
        Func<CancellationToken, Task<float[]>> embed,
        Func<T, float[], T> withEmbedding,
        Func<T, CancellationToken, Task<T>> upsert,
        CancellationToken cancellationToken)
    {
        var final = item;
        if (shouldEmbed)
        {
            var embedding = await embed(cancellationToken);
            final = withEmbedding(item, embedding);
        }
        return await upsert(final, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Fact>> GetFactsBySubjectAsync(
        string subject,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        return _factRepo.GetBySubjectAsync(subject, scope, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Fact>> SearchFactsAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        var scored = await _factRepo.SearchByVectorAsync(queryEmbedding, limit, minScore, scope, cancellationToken);
        return scored.Select(r => r.Fact).ToList();
    }

    /// <inheritdoc/>
    public Task<Relationship> AddRelationshipAsync(
        Relationship relationship,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relationship);
        return _relRepo.UpsertAsync(relationship, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Relationship>> GetEntityRelationshipsAsync(
        string entityId,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        return _relRepo.GetByEntityAsync(entityId, scope, cancellationToken);
    }

    /// <inheritdoc/>
    public Task DeletePreferenceAsync(
        string preferenceId,
        AgentMemory.Abstractions.Options.MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Deleting preference {PreferenceId}, owner={Owner}", preferenceId, scope?.OwnerId);
        return _prefRepo.DeleteAsync(preferenceId, scope, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Entity>> SearchEntitiesAsOfAsync(
        float[] queryEmbedding,
        DateTimeOffset asOf,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        var scored = await _entityRepo.SearchByVectorAsOfAsync(queryEmbedding, asOf, limit, minScore, scope, cancellationToken);
        return scored.Select(r => r.Entity).ToList();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Fact>> SearchFactsAsOfAsync(
        float[] queryEmbedding,
        DateTimeOffset asOf,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        var scored = await _factRepo.SearchByVectorAsOfAsync(queryEmbedding, asOf, limit, minScore, scope, cancellationToken);
        return scored.Select(r => r.Fact).ToList();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Preference>> SearchPreferencesAsOfAsync(
        float[] queryEmbedding,
        DateTimeOffset asOf,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        var scored = await _prefRepo.SearchByVectorAsOfAsync(queryEmbedding, asOf, limit, minScore, scope, cancellationToken);
        return scored.Select(r => r.Preference).ToList();
    }
}
