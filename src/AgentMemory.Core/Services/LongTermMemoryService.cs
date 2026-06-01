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
        CancellationToken cancellationToken = default)
    {
        return _entityRepo.GetByNameAsync(name, includeAliases, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Entity>> SearchEntitiesAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default)
    {
        var scored = await _entityRepo.SearchByVectorAsync(queryEmbedding, limit, minScore, cancellationToken);
        return scored.Select(r => r.Entity).ToList();
    }

    /// <inheritdoc/>
    public Task<Preference> AddPreferenceAsync(
        Preference preference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preference);
        return EnsureEmbeddingThenUpsertAsync(
            preference,
            shouldEmbed: _options.GeneratePreferenceEmbeddings && preference.Embedding is null,
            embed: ct =>
            {
                _logger.LogDebug("Generating embedding for preference {PreferenceId}", preference.PreferenceId);
                return _embeddingOrchestrator.EmbedPreferenceAsync(preference.PreferenceText, ct);
            },
            withEmbedding: (p, emb) => p with { Embedding = emb },
            upsert: _prefRepo.UpsertAsync,
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Preference>> GetPreferencesByCategoryAsync(
        string category,
        CancellationToken cancellationToken = default)
    {
        return _prefRepo.GetByCategoryAsync(category, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Preference>> SearchPreferencesAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default)
    {
        var scored = await _prefRepo.SearchByVectorAsync(queryEmbedding, limit, minScore, cancellationToken);
        return scored.Select(r => r.Preference).ToList();
    }

    /// <inheritdoc/>
    public Task<Fact> AddFactAsync(
        Fact fact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fact);
        return EnsureEmbeddingThenUpsertAsync(
            fact,
            shouldEmbed: _options.GenerateFactEmbeddings && fact.Embedding is null,
            embed: ct =>
            {
                _logger.LogDebug("Generating embedding for fact {FactId}", fact.FactId);
                return _embeddingOrchestrator.EmbedFactAsync(fact.Subject, fact.Predicate, fact.Object, ct);
            },
            withEmbedding: (f, emb) => f with { Embedding = emb },
            upsert: _factRepo.UpsertAsync,
            cancellationToken);
    }

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
        CancellationToken cancellationToken = default)
    {
        return _factRepo.GetBySubjectAsync(subject, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Fact>> SearchFactsAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default)
    {
        var scored = await _factRepo.SearchByVectorAsync(queryEmbedding, limit, minScore, cancellationToken);
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
        CancellationToken cancellationToken = default)
    {
        return _relRepo.GetByEntityAsync(entityId, cancellationToken);
    }

    /// <inheritdoc/>
    public Task DeletePreferenceAsync(
        string preferenceId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Deleting preference {PreferenceId}", preferenceId);
        return _prefRepo.DeleteAsync(preferenceId, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Entity>> SearchEntitiesAsOfAsync(
        float[] queryEmbedding,
        DateTimeOffset asOf,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default)
    {
        var scored = await _entityRepo.SearchByVectorAsOfAsync(queryEmbedding, asOf, limit, minScore, cancellationToken);
        return scored.Select(r => r.Entity).ToList();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Fact>> SearchFactsAsOfAsync(
        float[] queryEmbedding,
        DateTimeOffset asOf,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default)
    {
        var scored = await _factRepo.SearchByVectorAsOfAsync(queryEmbedding, asOf, limit, minScore, cancellationToken);
        return scored.Select(r => r.Fact).ToList();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Preference>> SearchPreferencesAsOfAsync(
        float[] queryEmbedding,
        DateTimeOffset asOf,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default)
    {
        var scored = await _prefRepo.SearchByVectorAsOfAsync(queryEmbedding, asOf, limit, minScore, cancellationToken);
        return scored.Select(r => r.Preference).ToList();
    }
}
