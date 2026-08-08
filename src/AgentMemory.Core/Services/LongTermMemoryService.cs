using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Core.Memory;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Core.Services;

/// <summary>
/// Service for long-term (structured knowledge) memory operations.
/// </summary>
internal sealed class LongTermMemoryService : ILongTermMemoryService
{
    private const int FactDedupLockStripeCount = 256;
    private static readonly SemaphoreSlim[] FactDedupLockStripes =
        Enumerable.Range(0, FactDedupLockStripeCount).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    private readonly IEntityRepository _entityRepo;
    private readonly IFactRepository _factRepo;
    private readonly IPreferenceRepository _prefRepo;
    private readonly IRelationshipRepository _relRepo;
    private readonly IEmbeddingOrchestrator _embeddingOrchestrator;
    private readonly LongTermMemoryOptions _options;
    private readonly ILogger<LongTermMemoryService> _logger;
    private readonly IMemoryIsolationPolicy _isolationPolicy;

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
        ILogger<LongTermMemoryService> logger,
        IMemoryIsolationPolicy isolationPolicy)
    {
        ArgumentNullException.ThrowIfNull(entityRepo);
        ArgumentNullException.ThrowIfNull(factRepo);
        ArgumentNullException.ThrowIfNull(prefRepo);
        ArgumentNullException.ThrowIfNull(relRepo);
        ArgumentNullException.ThrowIfNull(embeddingOrchestrator);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(isolationPolicy);

        _entityRepo = entityRepo;
        _factRepo = factRepo;
        _prefRepo = prefRepo;
        _relRepo = relRepo;
        _embeddingOrchestrator = embeddingOrchestrator;
        _options = options.Value;
        _logger = logger;
        _isolationPolicy = isolationPolicy;
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

        var resolvedScope = Resolve(scope, nameof(RecordEntityFeedbackAsync));

        var magnitude = Math.Abs(delta ?? _options.FeedbackConfidenceDelta);
        var signed = positive ? magnitude : -magnitude;

        _logger.LogDebug(
            "Recording {Kind} feedback ({Delta}) for entity {EntityId}, owner={Owner}",
            positive ? "positive" : "negative", signed, entityId, resolvedScope.OwnerId);
        return _entityRepo.ApplyConfidenceDeltaAsync(entityId, signed, resolvedScope, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<Entity> AddEntityAsync(
        Entity entity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        entity = entity with { OwnerId = ResolveOwner(entity.OwnerId, nameof(AddEntityAsync)) };

        if (entity.Confidence < _options.MinConfidenceThreshold)
        {
            _logger.LogDebug(
                "Skipping entity '{Id}' — confidence {Confidence} below LongTerm.MinConfidenceThreshold {Threshold}; not persisted.",
                entity.EntityId, entity.Confidence, _options.MinConfidenceThreshold);
            return Task.FromResult(entity);
        }

        return EnsureEmbeddingThenUpsertAsync(
            entity,
            shouldEmbed: _options.GenerateEntityEmbeddings && entity.Embedding is null,
            embed: cancellationToken =>
            {
                var text = string.IsNullOrEmpty(entity.Description) ? entity.Name : $"{entity.Name}: {entity.Description}";
                _logger.LogDebug("Generating embedding for entity {EntityId}", entity.EntityId);
                return _embeddingOrchestrator.EmbedTextAsync(text, cancellationToken);
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
        return _entityRepo.GetByNameAsync(name, includeAliases, Resolve(scope, nameof(GetEntitiesByNameAsync)), cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Entity>> SearchEntitiesAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        var scored = await _entityRepo.SearchByVectorAsync(queryEmbedding, limit, minScore, Resolve(scope, nameof(SearchEntitiesAsync)), cancellationToken).ConfigureAwait(false);
        return scored.Select(r => r.Entity).ToList();
    }

    /// <inheritdoc/>
    public async Task<Preference> AddPreferenceAsync(
        Preference preference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preference);
        preference = preference with { OwnerId = ResolveOwner(preference.OwnerId, nameof(AddPreferenceAsync)) };

        if (preference.Confidence < _options.MinConfidenceThreshold)
        {
            _logger.LogDebug(
                "Skipping preference '{Id}' — confidence {Confidence} below LongTerm.MinConfidenceThreshold {Threshold}; not persisted.",
                preference.PreferenceId, preference.Confidence, _options.MinConfidenceThreshold);
            return preference;
        }

        var embedding = preference.Embedding;
        if (_options.GeneratePreferenceEmbeddings && embedding is null)
        {
            _logger.LogDebug("Generating embedding for preference {PreferenceId}", preference.PreferenceId);
            embedding = await _embeddingOrchestrator.EmbedPreferenceAsync(preference.PreferenceText, cancellationToken).ConfigureAwait(false);
        }

        // Dedup-on-create: reinforce an existing same-category, same-owner near-duplicate instead of
        // creating a new node (preferences MERGE on id, so every add would otherwise be a fresh node).
        // Length-check (not just non-null): EmbeddingOrchestrator degrades a generation failure to an EMPTY
        // (zero-dimension) vector, which would otherwise be handed to db.index.vector.queryNodes and throw a
        // dimension mismatch — aborting the whole add. An empty embedding has no semantic signal, so skip
        // dedup and fall through to a plain create (the node persists with a NULL, re-queueable embedding).
        if (_options.DeduplicateOnCreate && embedding is { Length: > 0 })
        {
            var dup = await _prefRepo.FindDuplicateAsync(
                preference.Category, embedding, preference.OwnerId,
                _options.DeduplicationSimilarityThreshold, cancellationToken).ConfigureAwait(false);
            if (dup is not null)
            {
                var reinforced = BumpConfidence(dup.Confidence, preference.Confidence);
                var marked = await _prefRepo.MarkDeduplicatedAsync(dup.PreferenceId, reinforced, cancellationToken).ConfigureAwait(false);
                if (marked is not null)
                {
                    _logger.LogDebug("Deduplicated preference in '{Category}' onto {Id} (confidence→{C}).",
                        preference.Category, dup.PreferenceId, reinforced);
                    return marked;
                }
                // The duplicate was concurrently hard-deleted between find and reinforce — fall through and
                // create the new node instead of failing the add.
                _logger.LogDebug("Dedup target preference {Id} vanished before reinforce; creating new node.", dup.PreferenceId);
            }
        }

        var toSave = embedding is null ? preference : preference with { Embedding = embedding };
        return await _prefRepo.UpsertAsync(toSave, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Preference>> GetPreferencesByCategoryAsync(
        string category,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        return _prefRepo.GetByCategoryAsync(category, Resolve(scope, nameof(GetPreferencesByCategoryAsync)), cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Preference>> SearchPreferencesAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        var scored = await _prefRepo.SearchByVectorAsync(queryEmbedding, limit, minScore, Resolve(scope, nameof(SearchPreferencesAsync)), cancellationToken).ConfigureAwait(false);
        return scored.Select(r => r.Preference).ToList();
    }

    /// <inheritdoc/>
    public async Task<Fact> AddFactAsync(
        Fact fact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fact);
        fact = fact with { OwnerId = ResolveOwner(fact.OwnerId, nameof(AddFactAsync)) };

        if (fact.Confidence < _options.MinConfidenceThreshold)
        {
            _logger.LogDebug(
                "Skipping fact '{Id}' ({S} {P} {O}) — confidence {Confidence} below LongTerm.MinConfidenceThreshold {Threshold}; not persisted.",
                fact.FactId, fact.Subject, fact.Predicate, fact.Object, fact.Confidence, _options.MinConfidenceThreshold);
            return fact;
        }

        var embedding = fact.Embedding;
        if (_options.GenerateFactEmbeddings && embedding is null)
        {
            _logger.LogDebug("Generating embedding for fact {FactId}", fact.FactId);
            embedding = await _embeddingOrchestrator.EmbedFactAsync(fact.Subject, fact.Predicate, fact.Object, cancellationToken).ConfigureAwait(false);
        }

        // Dedup-on-create: reinforce an existing same-subject+predicate, same-owner near-duplicate
        // (e.g. the same fact phrased differently across sessions) instead of creating a new node.
        // Exact triples already collapse via the owner_key MERGE; this catches the near-duplicate case.
        // Length-check (not just non-null): EmbeddingOrchestrator degrades a generation failure to an EMPTY
        // (zero-dimension) vector, which would otherwise be handed to db.index.vector.queryNodes and throw a
        // dimension mismatch — aborting the whole add. An empty embedding has no semantic signal, so skip
        // dedup and fall through to a plain create (the node persists with a NULL, re-queueable embedding).
        var toSave = embedding is null ? fact : fact with { Embedding = embedding };
        if (!_options.DeduplicateOnCreate || embedding is not { Length: > 0 })
            return await _factRepo.UpsertAsync(toSave, cancellationToken).ConfigureAwait(false);

        // Find + reinforce/create is otherwise a TOCTOU race across concurrent request scopes. A bounded
        // process-wide stripe set serializes the same owner + case-insensitive subject/predicate key without
        // retaining one lock per memory forever. This deliberately provides in-process session correctness;
        // it does not claim distributed coordination between separate application instances.
        var dedupLock = FactDedupLock(toSave);
        await dedupLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var dup = await _factRepo.FindDuplicateAsync(
                fact.Subject, fact.Predicate, embedding, fact.OwnerId,
                _options.DeduplicationSimilarityThreshold, cancellationToken).ConfigureAwait(false);
            if (dup is not null)
            {
                var reinforced = BumpConfidence(dup.Confidence, fact.Confidence);
                var marked = await _factRepo.MarkDeduplicatedAsync(dup.FactId, reinforced, cancellationToken).ConfigureAwait(false);
                if (marked is not null)
                {
                    _logger.LogDebug("Deduplicated fact '{S} {P}' onto {Id} (confidence→{C}).",
                        fact.Subject, fact.Predicate, dup.FactId, reinforced);
                    return marked;
                }
                // The duplicate was concurrently hard-deleted between find and reinforce — fall through and
                // create the new node instead of failing the add.
                _logger.LogDebug("Dedup target fact {Id} vanished before reinforce; creating new node.", dup.FactId);
            }

            return await _factRepo.UpsertAsync(toSave, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            dedupLock.Release();
        }
    }

    private static SemaphoreSlim FactDedupLock(Fact fact)
    {
        var hash = new HashCode();
        hash.Add(fact.OwnerId, StringComparer.Ordinal);
        hash.Add(fact.Subject, StringComparer.OrdinalIgnoreCase);
        hash.Add(fact.Predicate, StringComparer.OrdinalIgnoreCase);
        return FactDedupLockStripes[(uint)hash.ToHashCode() % FactDedupLockStripeCount];
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
            var embedding = await embed(cancellationToken).ConfigureAwait(false);
            final = withEmbedding(item, embedding);
        }
        return await upsert(final, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Fact>> GetFactsBySubjectAsync(
        string subject,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        return _factRepo.GetBySubjectAsync(subject, Resolve(scope, nameof(GetFactsBySubjectAsync)), cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Fact>> SearchFactsAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default) =>
        SearchFactsAsync(
            queryEmbedding, limit, minScore, scope, false, 0, cancellationToken);

    /// <summary>
    /// Fact recall with optional canonical-predicate expansion (G5 "hard" tier).
    /// </summary>
    /// <remarks>
    /// A separate overload rather than optional parameters on the interface method: adding optional
    /// parameters to a published interface breaks every implementor, and the interface is locked
    /// under SemVer.
    /// </remarks>
    public Task<IReadOnlyList<Fact>> SearchFactsAsync(
        float[] queryEmbedding,
        int limit,
        double minScore,
        MemoryScope? scope,
        bool expandByPredicate,
        int expansionLimit,
        CancellationToken cancellationToken) =>
        SearchFactsAsync(
            queryEmbedding, limit, minScore, scope, expandByPredicate, expansionLimit,
            Array.Empty<string>(), cancellationToken);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Fact>> SearchFactsAsync(
        float[] queryEmbedding,
        int limit,
        double minScore,
        MemoryScope? scope,
        bool expandByPredicate,
        int expansionLimit,
        IReadOnlyList<string> questionRelations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(questionRelations);
        var resolved = Resolve(scope, nameof(SearchFactsAsync));
        var scored = await _factRepo.SearchByVectorAsync(queryEmbedding, limit, minScore, resolved, cancellationToken).ConfigureAwait(false);
        var top = scored.Select(r => r.Fact).ToList();
        // A question that names its relations outright does not need the top-K to nominate them, so an
        // empty top-K is only a dead end when there is nothing else to expand on.
        if (!expandByPredicate || (top.Count == 0 && questionRelations.Count == 0))
            return top;

        // G5 "hard" tier. Similarity decides *which* relation matters; this returns that relation
        // whole. Top-K is a relevance cutoff and carries no completeness guarantee, so a question
        // like "how many babies were born" is unanswerable from it - miss one of five and the count
        // is four. Expansion is additive: the similarity-ranked facts stay, in order, at the front.
        var predicates = top
            .Select(fact => MemoryTripleCanonicalizer.Canonical(fact.Predicate))
            // J2.2. Relations the question named, each widened to every form it could be stored under:
            // the write-side canonicalizer never folds morphology, so one relation lives under several
            // keys and expanding only the canonical name would miss the smaller buckets.
            .Concat(questionRelations.SelectMany(
                relation => MemoryRelationLexicon.Default.StoredFormsOf(relation)))
            .Where(predicate => predicate.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var expanded = await _factRepo.SearchByCanonicalPredicatesAsync(
            predicates, expansionLimit, resolved, cancellationToken).ConfigureAwait(false);

        var seen = top.Select(fact => fact.FactId).ToHashSet(StringComparer.Ordinal);
        foreach (var fact in expanded)
        {
            if (!seen.Add(fact.FactId))
                continue;

            // Marked because expansion returns a relation across the whole owner, so a fact may
            // legitimately carry provenance outside the current query's window. A consumer
            // resolving provenance must be able to tell that apart from a source that genuinely
            // cannot be resolved, which is corruption — marking the former keeps the latter
            // detectable rather than silencing both.
            var metadata = new Dictionary<string, object>(fact.Metadata, StringComparer.Ordinal)
            {
                [Fact.RetrievalSourceMetadataKey] = Fact.RetrievalSourcePredicateExpansion
            };
            top.Add(fact with { Metadata = metadata });
        }

        return top;
    }

    /// <inheritdoc/>
    public Task<Relationship> AddRelationshipAsync(
        Relationship relationship,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relationship);
        relationship = relationship with { OwnerId = ResolveOwner(relationship.OwnerId, nameof(AddRelationshipAsync)) };
        return _relRepo.UpsertAsync(relationship, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Relationship>> GetEntityRelationshipsAsync(
        string entityId,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        return _relRepo.GetByEntityAsync(entityId, Resolve(scope, nameof(GetEntityRelationshipsAsync)), cancellationToken);
    }

    /// <inheritdoc/>
    public Task DeletePreferenceAsync(
        string preferenceId,
        AgentMemory.Abstractions.Options.MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedScope = Resolve(scope, nameof(DeletePreferenceAsync));
        _logger.LogDebug("Deleting preference {PreferenceId}, owner={Owner}", preferenceId, resolvedScope.OwnerId);
        return _prefRepo.DeleteAsync(preferenceId, resolvedScope, cancellationToken);
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
        var scored = await _entityRepo.SearchByVectorAsOfAsync(queryEmbedding, asOf, limit, minScore, Resolve(scope, nameof(SearchEntitiesAsOfAsync)), cancellationToken).ConfigureAwait(false);
        return scored.Select(r => r.Entity).ToList();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Fact>> SearchFactsAsOfAsync(
        float[] queryEmbedding,
        DateTimeOffset asOf,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        DateTimeOffset? systemAsOf = null,
        CancellationToken cancellationToken = default)
    {
        var scored = await _factRepo.SearchByVectorAsOfAsync(queryEmbedding, asOf, limit, minScore, Resolve(scope, nameof(SearchFactsAsOfAsync)), systemAsOf, cancellationToken).ConfigureAwait(false);
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
        var scored = await _prefRepo.SearchByVectorAsOfAsync(queryEmbedding, asOf, limit, minScore, Resolve(scope, nameof(SearchPreferencesAsOfAsync)), cancellationToken).ConfigureAwait(false);
        return scored.Select(r => r.Preference).ToList();
    }

    // ── Invalidation & supersession (D5 / D7) — thin owner-scoped delegations, gated through the
    // central isolation policy (#100): these never had a fallback derivation of their own, so this adds
    // a warn/fail-closed gate on top of the existing pass-through rather than replacing anything. ──

    /// <inheritdoc/>
    public Task<bool> InvalidateFactAsync(string factId, MemoryScope? scope = null, CancellationToken cancellationToken = default)
        => _factRepo.InvalidateAsync(factId, Resolve(scope, nameof(InvalidateFactAsync)), cancellationToken);

    /// <inheritdoc/>
    public Task<bool> InvalidateEntityAsync(string entityId, MemoryScope? scope = null, CancellationToken cancellationToken = default)
        => _entityRepo.InvalidateAsync(entityId, Resolve(scope, nameof(InvalidateEntityAsync)), cancellationToken);

    /// <inheritdoc/>
    public Task<bool> InvalidatePreferenceAsync(string preferenceId, MemoryScope? scope = null, CancellationToken cancellationToken = default)
        => _prefRepo.InvalidateAsync(preferenceId, Resolve(scope, nameof(InvalidatePreferenceAsync)), cancellationToken);

    /// <inheritdoc/>
    public Task<bool> SupersedeFactAsync(string loserFactId, string winnerFactId, MemoryScope? scope = null, CancellationToken cancellationToken = default)
        => _factRepo.SupersedeAsync(loserFactId, winnerFactId, Resolve(scope, nameof(SupersedeFactAsync)), cancellationToken);

    /// <inheritdoc/>
    public Task<bool> SupersedePreferenceAsync(string loserPreferenceId, string winnerPreferenceId, MemoryScope? scope = null, CancellationToken cancellationToken = default)
        => _prefRepo.SupersedeAsync(loserPreferenceId, winnerPreferenceId, Resolve(scope, nameof(SupersedePreferenceAsync)), cancellationToken);

    // ── #100 Stage 2: every remaining read/write in this service now goes through the central policy
    // too, not just invalidate/supersede — a write with no owner (or a read with no scope) fails closed
    // under StrictMultiTenant instead of silently persisting as shared / reading global. ──

    /// <summary>Resolves the scope a read (or scope-shaped delete) should actually use.</summary>
    private MemoryScope Resolve(MemoryScope? scope, string operationName) =>
        _isolationPolicy.ResolveReadScope(scope, ownerId: null, operationName, MemoryOperationAccess.Tenant);

    /// <summary>Resolves the owner id a write should actually stamp onto the new/updated record.</summary>
    private string? ResolveOwner(string? ownerId, string operationName) =>
        _isolationPolicy.ResolveWriteOwner(ownerId, operationName, MemoryOperationAccess.Tenant);
}
