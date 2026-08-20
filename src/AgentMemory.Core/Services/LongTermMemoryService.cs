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
internal sealed class LongTermMemoryService : ILongTermMemoryService, IScoredLongTermSearch
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
    private readonly WorkingMemoryRebuilder _rebuilder;

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
        IMemoryIsolationPolicy isolationPolicy,
        // 30.4. Optional, mirroring the assembler's nullable IGraphRagContextSource: a host that has
        // not registered the working-memory tier keeps the exact previous construction shape.
        IWorkingMemoryService? workingMemory = null,
        IOptions<MemoryOptions>? memoryOptions = null)
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
        _rebuilder = new WorkingMemoryRebuilder(
            workingMemory,
            memoryOptions?.Value.WorkingMemory ?? new WorkingMemoryOptions(),
            _logger);
    }

    /// <summary>
    /// Rebuilds the owner's working-memory block after a write that changed long-term memory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Awaited inline, not fire-and-forget.</b> The contract the staleness canary tests is "after
    /// the write call returns, the block is current" — a fire-and-forget rebuild would trade that
    /// contract for a few milliseconds on a write that already cost about a second of extraction.
    /// </para>
    /// <para>
    /// <b>Never fails the write.</b> A rebuild is derived bookkeeping; a caller who successfully stored
    /// a fact must not see an exception because a projection of it could not be recompiled. On failure
    /// the stored block is CLEARED rather than left stale, because absence degrades to today's
    /// behaviour while staleness manufactures knowledge-update errors.
    /// </para>
    /// <para>
    /// <b>GUARD G3 lives at the other end</b> (<c>Neo4jWorkingMemoryService.ShouldSkip</c>): an
    /// ownerless write — which is what every TCK bridge write is — must not reach a MERGE on a null
    /// identity key.
    /// </para>
    /// </remarks>
    private Task RebuildWorkingMemoryAsync(string? ownerId, CancellationToken cancellationToken) =>
        _rebuilder.RebuildAsync(ownerId, "a long-term memory write", cancellationToken);

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
        var scored = await SearchEntitiesWithScoresAsync(queryEmbedding, limit, minScore, scope, cancellationToken).ConfigureAwait(false);
        return scored.Select(r => r.Entity).ToList();
    }

    /// <summary>
    /// Returns the repository's already-ranked entity results without a second query — see
    /// <see cref="IScoredLongTermSearch"/> for why this is a separate internal contract.
    /// </summary>
    /// <remarks>
    /// The isolation-policy operation name stays <c>SearchEntitiesAsync</c>: the policy logs and (in
    /// StrictMultiTenant) throws with that name, and asking for scores must not change what an operator
    /// sees in the audit trail.
    /// </remarks>
    public Task<IReadOnlyList<(Entity Entity, double Score)>> SearchEntitiesWithScoresAsync(
        float[] queryEmbedding,
        int limit,
        double minScore,
        MemoryScope? scope,
        CancellationToken cancellationToken) =>
        _entityRepo.SearchByVectorAsync(queryEmbedding, limit, minScore, Resolve(scope, nameof(SearchEntitiesAsync)), cancellationToken);

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
        var saved = await _prefRepo.UpsertAsync(toSave, cancellationToken).ConfigureAwait(false);
        await RebuildWorkingMemoryAsync(saved.OwnerId, cancellationToken).ConfigureAwait(false);
        return saved;
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
        var scored = await SearchPreferencesWithScoresAsync(queryEmbedding, limit, minScore, scope, cancellationToken).ConfigureAwait(false);
        return scored.Select(r => r.Preference).ToList();
    }

    /// <summary>
    /// Returns the repository's already-ranked preference results without a second query. Operation name
    /// pinned to <c>SearchPreferencesAsync</c> for the same reason as
    /// <see cref="SearchEntitiesWithScoresAsync"/>.
    /// </summary>
    public Task<IReadOnlyList<(Preference Preference, double Score)>> SearchPreferencesWithScoresAsync(
        float[] queryEmbedding,
        int limit,
        double minScore,
        MemoryScope? scope,
        CancellationToken cancellationToken) =>
        _prefRepo.SearchByVectorAsync(queryEmbedding, limit, minScore, Resolve(scope, nameof(SearchPreferencesAsync)), cancellationToken);

    /// <inheritdoc/>
    /// <remarks>
    /// A thin epilogue wrapper. The core below has three separate return branches (below-threshold,
    /// plain upsert, dedup-reinforce), and hanging the working-memory rebuild off each of them is how
    /// one branch quietly stops rebuilding — the design's own instruction was to route them all
    /// through a single epilogue.
    /// </remarks>
    public async Task<Fact> AddFactAsync(
        Fact fact,
        CancellationToken cancellationToken = default)
    {
        var saved = await AddFactCoreAsync(fact, cancellationToken).ConfigureAwait(false);
        await RebuildWorkingMemoryAsync(saved.OwnerId, cancellationToken).ConfigureAwait(false);
        return saved;
    }

    private async Task<Fact> AddFactCoreAsync(
        Fact fact,
        CancellationToken cancellationToken)
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
    public Task<IReadOnlyList<Fact>> SearchFactsAsync(
        float[] queryEmbedding,
        int limit,
        double minScore,
        MemoryScope? scope,
        bool expandByPredicate,
        int expansionLimit,
        IReadOnlyList<string> questionRelations,
        CancellationToken cancellationToken) =>
        SearchFactsCoreAsync(
            queryEmbedding, limit, minScore, scope, expandByPredicate, expansionLimit,
            questionRelations, scoreSink: null, cancellationToken);

    /// <inheritdoc/>
    /// <remarks>
    /// A straight forward to the repository, with the owner scope resolved through the isolation policy
    /// exactly as every other read here is. Note what this method does <b>not</b> do: it takes no query
    /// embedding and applies no score floor, because firing selects by time. A reminder is off-topic by
    /// definition, and a similarity-scoped version could never surface the ones that matter most.
    /// </remarks>
    public Task<ProspectiveDueResult> GetDueFactsAsync(
        DateTimeOffset since,
        DateTimeOffset now,
        TimeSpan expiringWindow,
        int limit,
        MemoryScope? scope,
        CancellationToken cancellationToken = default)
    {
        var resolved = _isolationPolicy.ResolveReadScope(
            scope, ownerId: null, nameof(GetDueFactsAsync), MemoryOperationAccess.Tenant);
        return _factRepo.GetDueFactsAsync(
            since, now, expiringWindow, limit, resolved, cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Scope resolved through the isolation policy like every other read. The <paramref name="minScore"/>
    /// arrives from the caller unchanged and deliberately: a tombstone must clear the same similarity
    /// bar a live fact would have, or it is a confident claim about having forgotten something on an
    /// unrelated topic.
    /// </remarks>
    public Task<IReadOnlyList<Fact>> SearchDecayedFactsAsync(
        float[] queryEmbedding,
        int limit,
        double minScore,
        MemoryScope? scope,
        CancellationToken cancellationToken = default)
    {
        var resolved = _isolationPolicy.ResolveReadScope(
            scope, ownerId: null, nameof(SearchDecayedFactsAsync), MemoryOperationAccess.Tenant);
        return _factRepo.SearchDecayedFactsAsync(
            queryEmbedding, limit, minScore, resolved, cancellationToken);
    }

    /// <summary>
    /// Fact recall that also hands back the similarity scores the vector index already produced — see
    /// <see cref="IScoredLongTermSearch"/>. Operation name pinned to <c>SearchFactsAsync</c> for the same
    /// reason as <see cref="SearchEntitiesWithScoresAsync"/>.
    /// </summary>
    public async Task<ScoredFactSearchResult> SearchFactsWithScoresAsync(
        float[] queryEmbedding,
        int limit,
        double minScore,
        MemoryScope? scope,
        bool expandByPredicate,
        int expansionLimit,
        IReadOnlyList<string> questionRelations,
        CancellationToken cancellationToken)
    {
        var scoreSink = new List<(Fact Fact, double Score)>();
        var facts = await SearchFactsCoreAsync(
            queryEmbedding, limit, minScore, scope, expandByPredicate, expansionLimit,
            questionRelations, scoreSink, cancellationToken).ConfigureAwait(false);
        return new ScoredFactSearchResult(facts, scoreSink);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Fact>> SearchFactsAsync(
        float[] queryEmbedding,
        ValidTimeMode validTime,
        int limit,
        double minScore,
        MemoryScope? scope,
        CancellationToken cancellationToken) =>
        SearchFactsCoreAsync(
            queryEmbedding, limit, minScore, scope, expandByPredicate: false, expansionLimit: 0,
            questionRelations: Array.Empty<string>(), scoreSink: null, cancellationToken, validTime);

    /// <summary>
    /// The single fact-recall implementation. <paramref name="scoreSink"/> is a sink rather than a second
    /// return value so the ordinary (diagnostics-off) call keeps exactly its previous allocations: null
    /// sink ⇒ one null check and nothing else.
    /// </summary>
    private async Task<IReadOnlyList<Fact>> SearchFactsCoreAsync(
        float[] queryEmbedding,
        int limit,
        double minScore,
        MemoryScope? scope,
        bool expandByPredicate,
        int expansionLimit,
        IReadOnlyList<string> questionRelations,
        List<(Fact Fact, double Score)>? scoreSink,
        CancellationToken cancellationToken,
        ValidTimeMode validTime = ValidTimeMode.Ignore)
    {
        ArgumentNullException.ThrowIfNull(questionRelations);
        var resolved = Resolve(scope, nameof(SearchFactsAsync));
        // Take the pre-existing overload unless the gate is actually on, so an ungated recall emits the
        // exact repository call it always did -- byte-identical rather than merely equivalent. It also
        // means a third-party IFactRepository that never implements the valid-time overload is only
        // reached through it when a caller explicitly asked for gating.
        var scored = validTime == ValidTimeMode.Ignore
            ? await _factRepo
                .SearchByVectorAsync(queryEmbedding, limit, minScore, resolved, cancellationToken)
                .ConfigureAwait(false)
            : await _factRepo
                .SearchByVectorAsync(queryEmbedding, validTime, limit, minScore, resolved, cancellationToken)
                .ConfigureAwait(false);
        // Only the similarity search scores anything. Expansion below appends facts fetched by predicate,
        // which carry no comparable score and are deliberately left out of the sink rather than given a
        // stand-in one.
        scoreSink?.AddRange(scored);
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
        // J3.1. The predicate set above deliberately mixes two very different things: relations the
        // QUESTION named, and predicates BORROWED from whatever top-K happened to return. They share
        // one budget ordered by confidence, so a borrowed predicate with high-confidence facts can
        // exhaust it before the named relation is complete - which defeats the completeness guarantee
        // this method exists to provide. Measured before fixing: a question holding 49 facts under
        // planned/plans, with a 60-row budget, received 22.
        //
        // Passing the named relations as priority makes them a tiebreak ahead of the borrowed ones.
        // Empty when the question named nothing, so the ordering is unchanged for every other path.
        var priorityPredicates = questionRelations
            .SelectMany(MemoryRelationLexicon.Default.StoredFormsOf)
            .Where(predicate => predicate.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var expanded = await _factRepo.SearchByCanonicalPredicatesAsync(
            predicates, expansionLimit, resolved, cancellationToken, priorityPredicates)
            .ConfigureAwait(false);

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

        // 30.4b: same staleness shape as invalidation — a deleted preference must leave the block.
        //
        // Rebuilt UNCONDITIONALLY, unlike the invalidate family, and the asymmetry is forced rather
        // than chosen: IPreferenceRepository.DeleteAsync returns Task, not Task<bool>, so there is no
        // way to learn whether the delete matched anything. The invalidate paths skip the rebuild on a
        // no-op precisely because they CAN tell. Here the choice is between a wasted rebuild on a
        // no-op delete and a stale block on a real one, and staleness is the failure this design
        // exists to prevent. Widening the repository signature would be the real fix; it is a public
        // interface locked under SemVer since 1.0, so it is not worth a break for a bookkeeping win.
        return DeleteThenRebuildAsync();

        async Task DeleteThenRebuildAsync()
        {
            await _prefRepo.DeleteAsync(preferenceId, resolvedScope, cancellationToken)
                .ConfigureAwait(false);
            await _rebuilder
                .RebuildAsync(resolvedScope.OwnerId, "a preference delete", cancellationToken)
                .ConfigureAwait(false);
        }
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
        var scored = await SearchEntitiesAsOfWithScoresAsync(queryEmbedding, asOf, limit, minScore, scope, cancellationToken).ConfigureAwait(false);
        return scored.Select(r => r.Entity).ToList();
    }

    /// <summary>
    /// Point-in-time entity search that keeps its scores. Operation name pinned to
    /// <c>SearchEntitiesAsOfAsync</c> for the same reason as <see cref="SearchEntitiesWithScoresAsync"/>.
    /// </summary>
    public Task<IReadOnlyList<(Entity Entity, double Score)>> SearchEntitiesAsOfWithScoresAsync(
        float[] queryEmbedding,
        DateTimeOffset asOf,
        int limit,
        double minScore,
        MemoryScope? scope,
        CancellationToken cancellationToken) =>
        _entityRepo.SearchByVectorAsOfAsync(queryEmbedding, asOf, limit, minScore, Resolve(scope, nameof(SearchEntitiesAsOfAsync)), cancellationToken);

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
        var scored = await SearchFactsAsOfWithScoresAsync(queryEmbedding, asOf, limit, minScore, scope, systemAsOf, cancellationToken).ConfigureAwait(false);
        return scored.Select(r => r.Fact).ToList();
    }

    /// <summary>
    /// Point-in-time fact search that keeps its scores. Unlike the live path this has no predicate
    /// expansion, so every returned fact is scored and a plain scored list suffices. Operation name pinned
    /// to <c>SearchFactsAsOfAsync</c> for the same reason as <see cref="SearchEntitiesWithScoresAsync"/>.
    /// </summary>
    public Task<IReadOnlyList<(Fact Fact, double Score)>> SearchFactsAsOfWithScoresAsync(
        float[] queryEmbedding,
        DateTimeOffset asOf,
        int limit,
        double minScore,
        MemoryScope? scope,
        DateTimeOffset? systemAsOf,
        CancellationToken cancellationToken) =>
        _factRepo.SearchByVectorAsOfAsync(queryEmbedding, asOf, limit, minScore, Resolve(scope, nameof(SearchFactsAsOfAsync)), systemAsOf, cancellationToken);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Preference>> SearchPreferencesAsOfAsync(
        float[] queryEmbedding,
        DateTimeOffset asOf,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        var scored = await SearchPreferencesAsOfWithScoresAsync(queryEmbedding, asOf, limit, minScore, scope, cancellationToken).ConfigureAwait(false);
        return scored.Select(r => r.Preference).ToList();
    }

    /// <summary>
    /// Point-in-time preference search that keeps its scores. Operation name pinned to
    /// <c>SearchPreferencesAsOfAsync</c> for the same reason as
    /// <see cref="SearchEntitiesWithScoresAsync"/>.
    /// </summary>
    public Task<IReadOnlyList<(Preference Preference, double Score)>> SearchPreferencesAsOfWithScoresAsync(
        float[] queryEmbedding,
        DateTimeOffset asOf,
        int limit,
        double minScore,
        MemoryScope? scope,
        CancellationToken cancellationToken) =>
        _prefRepo.SearchByVectorAsOfAsync(queryEmbedding, asOf, limit, minScore, Resolve(scope, nameof(SearchPreferencesAsOfAsync)), cancellationToken);

    // ── Invalidation & supersession (D5 / D7) — thin owner-scoped delegations, gated through the
    // central isolation policy (#100): these never had a fallback derivation of their own, so this adds
    // a warn/fail-closed gate on top of the existing pass-through rather than replacing anything. ──

    /// <summary>
    /// Shared epilogue for the invalidate/delete family (30.4b): perform the write, and recompile the
    /// owner's working-memory block when it actually removed something.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why these needed hooking at all.</b> Every working-memory block query filters
    /// <c>invalidated_at IS NULL</c>. Retracting a fact therefore changes what the block should say —
    /// but nothing recompiled it, so the block kept asserting the retracted value until some unrelated
    /// write happened to trigger a rebuild. That is precisely the staleness this design calls
    /// "manufactures knowledge-update failures", and it is the reason supersession is hooked.
    /// <b>Supersede was hooked and its exact twin was not.</b>
    /// </para>
    /// <para>
    /// Only on a <c>true</c> result: a scoped call that matched nothing changed nothing, and rebuilding
    /// there would spend reads on exactly the calls the isolation guard exists to make free.
    /// </para>
    /// </remarks>
    private async Task<bool> InvalidateThenRebuildAsync(
        Func<MemoryScope, Task<bool>> invalidate,
        MemoryScope? scope,
        string caller,
        CancellationToken cancellationToken)
    {
        var resolvedScope = Resolve(scope, caller);
        var changed = await invalidate(resolvedScope).ConfigureAwait(false);
        if (!changed) return false;

        await _rebuilder
            .RebuildAsync(resolvedScope.OwnerId, "an invalidation", cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public Task<bool> InvalidateFactAsync(string factId, MemoryScope? scope = null, CancellationToken cancellationToken = default)
        => InvalidateThenRebuildAsync(
            resolved => _factRepo.InvalidateAsync(factId, resolved, cancellationToken),
            scope, nameof(InvalidateFactAsync), cancellationToken);

    /// <inheritdoc/>
    public Task<bool> InvalidateEntityAsync(string entityId, MemoryScope? scope = null, CancellationToken cancellationToken = default)
        => InvalidateThenRebuildAsync(
            resolved => _entityRepo.InvalidateAsync(entityId, resolved, cancellationToken),
            scope, nameof(InvalidateEntityAsync), cancellationToken);

    /// <inheritdoc/>
    public Task<bool> InvalidatePreferenceAsync(string preferenceId, MemoryScope? scope = null, CancellationToken cancellationToken = default)
        => InvalidateThenRebuildAsync(
            resolved => _prefRepo.InvalidateAsync(preferenceId, resolved, cancellationToken),
            scope, nameof(InvalidatePreferenceAsync), cancellationToken);

    /// <inheritdoc/>
    /// <remarks>
    /// Rebuilds the working-memory block on success. THIS is the call the staleness canary exercises:
    /// supersession is the write that makes a block wrong, and a block asserting a superseded value
    /// would manufacture failures in the weakest measured question type.
    /// </remarks>
    public async Task<bool> SupersedeFactAsync(string loserFactId, string winnerFactId, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(scope, nameof(SupersedeFactAsync));
        var superseded = await _factRepo
            .SupersedeAsync(loserFactId, winnerFactId, resolved, cancellationToken).ConfigureAwait(false);
        if (superseded)
            await RebuildWorkingMemoryAsync(resolved?.OwnerId, cancellationToken).ConfigureAwait(false);

        return superseded;
    }

    /// <inheritdoc/>
    public async Task<bool> SupersedePreferenceAsync(string loserPreferenceId, string winnerPreferenceId, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(scope, nameof(SupersedePreferenceAsync));
        var superseded = await _prefRepo
            .SupersedeAsync(loserPreferenceId, winnerPreferenceId, resolved, cancellationToken).ConfigureAwait(false);
        if (superseded)
            await RebuildWorkingMemoryAsync(resolved?.OwnerId, cancellationToken).ConfigureAwait(false);

        return superseded;
    }

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

/// <summary>
/// Internal scored-search capability implemented by the built-in long-term memory service. Every one of
/// these searches is scored by the vector index and the score is then dropped on the way out of
/// <see cref="ILongTermMemoryService"/>; this contract recovers it <b>without issuing a second query</b>,
/// so <c>MemoryContextSection.RankedItems</c> can be populated for facts, entities and preferences.
/// Kept separate from the public interface for the same reason as <see cref="IScoredMessageSearch"/>:
/// the interface is SemVer-locked and adding members would break every external implementor.
/// </summary>
internal interface IScoredLongTermSearch
{
    Task<IReadOnlyList<(Entity Entity, double Score)>> SearchEntitiesWithScoresAsync(
        float[] queryEmbedding,
        int limit,
        double minScore,
        MemoryScope? scope,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<(Preference Preference, double Score)>> SearchPreferencesWithScoresAsync(
        float[] queryEmbedding,
        int limit,
        double minScore,
        MemoryScope? scope,
        CancellationToken cancellationToken);

    Task<ScoredFactSearchResult> SearchFactsWithScoresAsync(
        float[] queryEmbedding,
        int limit,
        double minScore,
        MemoryScope? scope,
        bool expandByPredicate,
        int expansionLimit,
        IReadOnlyList<string> questionRelations,
        CancellationToken cancellationToken);

    // Point-in-time siblings. Bitemporal recall assembles the same five sections from a second code path,
    // so leaving these out would have shipped the instrument to only one of the two recall paths.

    Task<IReadOnlyList<(Entity Entity, double Score)>> SearchEntitiesAsOfWithScoresAsync(
        float[] queryEmbedding,
        DateTimeOffset asOf,
        int limit,
        double minScore,
        MemoryScope? scope,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<(Preference Preference, double Score)>> SearchPreferencesAsOfWithScoresAsync(
        float[] queryEmbedding,
        DateTimeOffset asOf,
        int limit,
        double minScore,
        MemoryScope? scope,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<(Fact Fact, double Score)>> SearchFactsAsOfWithScoresAsync(
        float[] queryEmbedding,
        DateTimeOffset asOf,
        int limit,
        double minScore,
        MemoryScope? scope,
        DateTimeOffset? systemAsOf,
        CancellationToken cancellationToken);
}

/// <summary>
/// Fact recall split into the two things a diagnostics-enabled caller needs.
/// </summary>
/// <param name="Facts">
/// Every fact that goes into the context, predicate expansion included — identical, in content and order,
/// to what <c>SearchFactsAsync</c> returns for the same arguments.
/// </param>
/// <param name="Scored">
/// The subset the vector index actually scored. Expansion facts are retrieved by predicate, not by
/// similarity, so they have no score and are absent here rather than carrying a fabricated one — which is
/// what lets a reader tell "retrieved weakly" apart from "not ranked at all".
/// </param>
internal sealed record ScoredFactSearchResult(
    IReadOnlyList<Fact> Facts,
    IReadOnlyList<(Fact Fact, double Score)> Scored);
