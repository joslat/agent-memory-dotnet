using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;

namespace AgentMemory.Core.Extraction;

/// <summary>
/// Embeds and persists the resolved items from <see cref="ExtractionStage"/>.
/// Responsibility: generate embeddings, upsert to repositories, wire EXTRACTED_FROM provenance.
/// </summary>
internal sealed partial class PersistenceStage : IPersistenceStage
{
    private readonly IEmbeddingOrchestrator _embeddingOrchestrator;
    private readonly IEntityRepository _entityRepository;
    private readonly IFactRepository _factRepository;
    private readonly IPreferenceRepository _preferenceRepository;
    private readonly IRelationshipRepository _relationshipRepository;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private readonly ExtractionOptions _options;
    private readonly IMemoryPersistenceTransaction _persistenceTransaction;
    private readonly ILogger<PersistenceStage> _logger;
    private readonly WorkingMemoryRebuilder _rebuilder;

    public PersistenceStage(
        IEmbeddingOrchestrator embeddingOrchestrator,
        IEntityRepository entityRepository,
        IFactRepository factRepository,
        IPreferenceRepository preferenceRepository,
        IRelationshipRepository relationshipRepository,
        IClock clock,
        IIdGenerator idGenerator,
        ILogger<PersistenceStage> logger,
        IMemoryPersistenceTransaction persistenceTransaction,
        IOptions<ExtractionOptions>? extractionOptions = null,
        // 30.4. Optional and last, mirroring LongTermMemoryService: a host that has not registered the
        // working-memory tier keeps the exact previous construction shape.
        IWorkingMemoryService? workingMemory = null,
        IOptions<MemoryOptions>? memoryOptions = null)
    {
        _embeddingOrchestrator = embeddingOrchestrator;
        _entityRepository = entityRepository;
        _factRepository = factRepository;
        _preferenceRepository = preferenceRepository;
        _relationshipRepository = relationshipRepository;
        _clock = clock;
        _idGenerator = idGenerator;
        _logger = logger;
        _persistenceTransaction = persistenceTransaction ?? throw new ArgumentNullException(nameof(persistenceTransaction));
        _options = extractionOptions?.Value ?? new ExtractionOptions();
        _rebuilder = new WorkingMemoryRebuilder(
            workingMemory,
            memoryOptions?.Value.WorkingMemory ?? new WorkingMemoryOptions(),
            _logger);
    }

    public async Task<PersistenceResult> PersistAsync(
        ExtractionStageResult extraction,
        string? ownerId = null,
        MemoryTrustLevel trustLevel = MemoryTrustLevel.Untrusted,
        CancellationToken cancellationToken = default)
    {
        var result = await PersistCoreAsync(extraction, ownerId, trustLevel, cancellationToken)
            .ConfigureAwait(false);

        // Once per persist, and here rather than inside the core so it happens exactly once whichever
        // of the three return paths (atomic / best-effort / replay) produced the result, and outside
        // the storage transaction either way. A throw from the core skips it, which is right: nothing
        // was persisted, so there is nothing to recompile.
        await RebuildWorkingMemoryAsync(ownerId, result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Recompiles the owner's working-memory block after a persist that changed something.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This was missing, and its absence made the tier inert on the primary path.</b> The rebuild
    /// hook shipped only on <c>LongTermMemoryService</c>'s single-add methods, so a host that enabled
    /// the tier and then ingested normally — conversation, extraction, persist, which is what the MAF
    /// adapter does — never compiled a block at all. Recall fetched null forever and the feature read
    /// as enabled. Every existing test called <c>RebuildAsync</c> directly, so none of them could see it.
    /// </para>
    /// <para>
    /// Failure never propagates: the write already succeeded and the caller is not waiting on a derived
    /// projection. Staleness is worse than absence, so a failed rebuild clears the block when
    /// <c>ClearOnRebuildFailure</c> is set — the same contract the single-add path honours.
    /// </para>
    /// <para>
    /// <b>Here rather than in <c>MemoryExtractionPipeline</c> beside the session accountant</b>, which is
    /// the other post-persist hook and was the obvious alternative. Two reasons: both pipeline paths
    /// (single-session and batch) go through <c>PersistAsync</c>, as would any future direct
    /// <c>IPersistenceStage</c> caller; and the "at least one thing landed" gate the design specifies is
    /// the <see cref="PersistenceResult"/> counts, which are here and would otherwise need plumbing out.
    /// The cost is ordering: this runs before the accountant, so a fact <i>derived</i> in the same pass
    /// is one rebuild late. That needs a non-default <c>MinFactMentionCount</c> of 1 to be observable at
    /// all (derived facts are created with <c>mention_count = 1</c>) and self-heals on the next write,
    /// which is within what an eager hash-short-circuited rebuild already promises.
    /// </para>
    /// </remarks>
    private Task RebuildWorkingMemoryAsync(
        string? ownerId, PersistenceResult result, CancellationToken cancellationToken)
    {
        if (_rebuilder.IsDisabled) return Task.CompletedTask;

        // Nothing landed, nothing to recompile. Relationships are excluded on purpose: the block is
        // compiled from facts, preferences and entities, so a relationship-only persist cannot change it.
        if (result.EntityCount + result.FactCount + result.PreferenceCount == 0) return Task.CompletedTask;

        return _rebuilder.RebuildAsync(ownerId, "a persist", cancellationToken);
    }

    private async Task<PersistenceResult> PersistCoreAsync(
        ExtractionStageResult extraction,
        string? ownerId,
        MemoryTrustLevel trustLevel,
        CancellationToken cancellationToken)
    {
        // External embedding work is deliberately completed before the storage transaction opens.
        // Holding a database transaction while waiting on a model/provider would amplify contention
        // and make provider latency part of the database failure surface.
        using var activity = AgentMemoryDiagnostics.Source.StartActivity("memory.persist.total");
        if (activity is not null)
        {
            activity.SetTag("memory.persist.entities", extraction.ResolvedEntityMap.Count);
            activity.SetTag("memory.persist.facts", extraction.FilteredFacts.Count);
            activity.SetTag("memory.persist.preferences", extraction.FilteredPreferences.Count);
            activity.SetTag("memory.persist.relationships", extraction.FilteredRelationships.Count);
        }

        var prepared = await PrepareEmbeddingsAsync(extraction, cancellationToken).ConfigureAwait(false);
        if (_options.FailureMode == IngestionFailureMode.FailFast)
        {
            try
            {
                return await _persistenceTransaction.ExecuteAsync(
                    ct => PersistPreparedAsync(extraction, ownerId, trustLevel, prepared, ct),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (MemoryIngestionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Transaction-entry, commit, and rollback-confirmation failures occur outside the
                // per-item catch blocks below. Preserve the documented fail-fast boundary while
                // retaining the provider/transaction failure as the inner cause. Outcomes created
                // inside the rolled-back transaction are deliberately excluded as non-durable.
                var completedOutcomes = extraction.Outcomes.Concat(prepared.Outcomes).ToList();
                throw new MemoryIngestionException(
                    "Atomic memory persistence failed.", completedOutcomes, ex);
            }
        }

        if (!_options.UseCoalescedPersistenceTransactions ||
            !_persistenceTransaction.SupportsAtomicRollback)
        {
            return await PersistPreparedAsync(
                extraction, ownerId, trustLevel, prepared, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await _persistenceTransaction.ExecuteAsync(
                async ct =>
                {
                    var result = await PersistPreparedAsync(
                        extraction, ownerId, trustLevel, prepared, ct).ConfigureAwait(false);
                    if (result.Outcomes.Any(outcome => outcome.Status == IngestionItemStatus.Failed))
                        throw new ReplayBestEffortPersistenceException();
                    return result;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (ReplayBestEffortPersistenceException)
        {
            // ExecuteAsync may surface this marker only after its provider rollback completed. Reuse
            // the already prepared embeddings and replay through today's item-isolated best-effort path.
            return await PersistPreparedAsync(
                extraction, ownerId, trustLevel, prepared, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class ReplayBestEffortPersistenceException : Exception;

    private async Task<PersistenceResult> PersistPreparedAsync(
        ExtractionStageResult extraction,
        string? ownerId,
        MemoryTrustLevel trustLevel,
        PreparedEmbeddings prepared,
        CancellationToken cancellationToken)
    {
        var sourceMessageIds = extraction.SourceMessageIds;
        var failFast = _options.FailureMode == IngestionFailureMode.FailFast;
        var outcomes = new List<IngestionItemOutcome>(extraction.Outcomes);
        outcomes.AddRange(prepared.Outcomes);

        // 1. Embed + upsert entities; build a name→persisted Entity map for relationship resolution.
        var persistedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
        var entityInputs = prepared.Entities.Select(pair =>
        {
            var effectiveTrustLevel = MaxTrustLevel(pair.Value.Metadata.GetTrustLevel(), trustLevel);
            return (Name: pair.Key, Item: pair.Value with
            {
                OwnerId = ownerId,
                Metadata = pair.Value.Metadata.WithTrustLevel(effectiveTrustLevel)
            });
        }).ToList();

        async Task RecordPersistedEntityAsync(string name, Entity persisted)
        {
            persistedEntityMap[name] = persisted;
            RecordSuccess(outcomes, MemoryItemKind.Entity, name, persisted.EntityId);

            foreach (var msgId in ExplicitProvenanceMessageIds(_entityRepository, sourceMessageIds))
            {
                try
                {
                    await _entityRepository.CreateExtractedFromRelationshipAsync(
                        persisted.EntityId, msgId, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to create EXTRACTED_FROM for entity '{Id}' → message '{MsgId}'.",
                        persisted.EntityId, msgId);
                    RecordFailureAndMaybeThrow(outcomes, failFast, MemoryItemKind.Entity, IngestionStage.Provenance,
                        MemoryErrorCodes.ProvenancePersistenceFailed, name, persisted.EntityId, ex,
                        $"Ingestion failed fast: provenance failed for entity '{name}'.");
                }
            }

            _logger.LogDebug("Persisted entity '{Name}' (id={Id}).", persisted.Name, persisted.EntityId);
        }

        async Task PersistEntityIndividuallyAsync(string name, Entity item)
        {
            try
            {
                var persisted = await _entityRepository.UpsertAsync(item, cancellationToken).ConfigureAwait(false);
                await RecordPersistedEntityAsync(name, persisted).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (MemoryIngestionException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error persisting entity '{Name}'.", name);
                RecordFailureAndMaybeThrow(outcomes, failFast, MemoryItemKind.Entity, IngestionStage.Persistence,
                    MemoryErrorCodes.EntityPersistenceFailed, name, null, ex,
                    $"Ingestion failed fast: persistence failed for entity '{name}'.");
            }
        }

        Dictionary<string, Entity>? batchedEntitiesById = null;
        var fusedEntityRepository = _options.UseCoalescedPersistenceTransactions
            ? _entityRepository as IFusedBatchMemoryRepository<Entity> : null;
        var batchEntityRepository = _entityRepository as IBatchMemoryRepository<Entity>;
        var canBatchEntities = _options.EnableBatchMemoryUpserts && !failFast &&
            entityInputs.Count > 0 &&
            entityInputs.Select(input => input.Item.EntityId).Distinct(StringComparer.Ordinal).Count() == entityInputs.Count &&
            (fusedEntityRepository is not null || (entityInputs.Count > 1 && batchEntityRepository is not null));
        if (canBatchEntities)
        {
            try
            {
                var items = entityInputs.Select(input => input.Item).ToList();
                var persisted = fusedEntityRepository is not null
                    ? await fusedEntityRepository.UpsertFusedBatchAsync(items, cancellationToken)
                        .ConfigureAwait(false)
                    : await batchEntityRepository!.UpsertBatchAsync(items, cancellationToken)
                        .ConfigureAwait(false);
                batchedEntitiesById = persisted.ToDictionary(entity => entity.EntityId, StringComparer.Ordinal);
                if (entityInputs.Any(input => !batchedEntitiesById.ContainsKey(input.Item.EntityId)))
                    throw new InvalidOperationException("The entity batch result omitted one or more input identifiers.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Atomic entity batch failed; replaying {Count} entities through the item path.",
                    entityInputs.Count);
                batchedEntitiesById = null;
            }
        }

        if (batchedEntitiesById is not null)
        {
            foreach (var input in entityInputs)
                await RecordPersistedEntityAsync(input.Name, batchedEntitiesById[input.Item.EntityId]).ConfigureAwait(false);
        }
        else
        {
            foreach (var input in entityInputs)
                await PersistEntityIndividuallyAsync(input.Name, input.Item).ConfigureAwait(false);
        }
        // 2. Embed + upsert facts.
        var persistedFactCount = 0;

        async Task<(Fact Item, string SourceKey)?> PrepareFactAsync(PreparedFact preparedFact)
        {
            var extracted = preparedFact.Item;
            var factSourceKey = $"{extracted.Subject} {extracted.Predicate} {extracted.Object}";
            try
            {
                // Trust is monotonic for owner-scoped facts. The pre-fetch deliberately excludes shared
                // facts and carries an existing triple's casing forward so an exact MERGE cannot create a
                // casing-only duplicate. Rank 20 will make this read-modify-write atomic; feat-04 leaves
                // that owner boundary and trust behavior unchanged.
                Fact? existingFact = string.IsNullOrEmpty(ownerId)
                    ? null
                    : await _factRepository.FindByTripleAsync(
                        extracted.Subject, extracted.Predicate, extracted.Object,
                        MemoryScope.For(ownerId, includeShared: false), cancellationToken).ConfigureAwait(false);
                // Per-item refinement before the existing per-batch composition. At defaults SourceRole
                // is null on every item and this is the identity, so the trust a host sees is byte-for-
                // byte what it was; it becomes non-trivial only when assistant content is extracted,
                // which is the exact moment "who claimed this" stops being answerable from the batch.
                var requestTrustLevel = SourceRoleTrust.Refine(trustLevel, extracted.SourceRole);
                // Per-item provenance (L3c). Identity at defaults -- SourceTurn is null unless
                // ExtractionProvenanceMode.PerItem asked for it -- and a reported turn that does not
                // resolve keeps the batch links rather than inventing a narrower wrong one.
                var factMessageIds = SourceTurnProvenance.Resolve(extracted.SourceTurn, sourceMessageIds);
                var effectiveFactTrustLevel = existingFact is null
                    ? requestTrustLevel
                    : MaxTrustLevel(existingFact.Metadata.GetTrustLevel(), requestTrustLevel);
                var factMetadata = existingFact is null
                    ? MemoryTrustMetadataExtensions.CreateWithTrustLevel(effectiveFactTrustLevel)
                    : existingFact.Metadata.WithTrustLevel(effectiveFactTrustLevel);

                return (new Fact
                {
                    FactId = _idGenerator.GenerateId(),
                    Subject = existingFact?.Subject ?? extracted.Subject,
                    Predicate = existingFact?.Predicate ?? extracted.Predicate,
                    Object = existingFact?.Object ?? extracted.Object,
                    Confidence = extracted.Confidence,
                    ValidFrom = extracted.ValidFrom,
                    ValidUntil = extracted.ValidUntil,
                    Embedding = preparedFact.Embedding,
                    OwnerId = ownerId,
                    SourceMessageIds = factMessageIds,
                    CreatedAtUtc = _clock.UtcNow,
                    Metadata = factMetadata
                }, factSourceKey);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error preparing fact '{Key}' for persistence.", factSourceKey);
                RecordFailureAndMaybeThrow(outcomes, failFast, MemoryItemKind.Fact, IngestionStage.Persistence,
                    MemoryErrorCodes.FactPersistenceFailed, factSourceKey, null, ex,
                    $"Ingestion failed fast: persistence failed for fact '{factSourceKey}'.");
                return null;
            }
        }

        async Task RecordPersistedFactAsync(
            string sourceKey, Fact persisted, IReadOnlyList<string> provenanceMessageIds)
        {
            // Fact upsert MERGEs on the natural triple and may return an older stable id. Always use
            // the repository result for outcomes and provenance rather than the fresh caller id.
            RecordSuccess(outcomes, MemoryItemKind.Fact, sourceKey, persisted.FactId);

            // The INPUT item's resolved ids, not the persisted result's: a MERGE returns the stored
            // node, whose source ids may be the union accumulated over earlier ingestions. Writing
            // edges from that would re-link this fact to messages it was not extracted from now --
            // which is exactly the batch-level breadth per-item provenance exists to remove.
            foreach (var msgId in ExplicitProvenanceMessageIds(_factRepository, provenanceMessageIds))
            {
                try
                {
                    await _factRepository.CreateExtractedFromRelationshipAsync(
                        persisted.FactId, msgId, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to create EXTRACTED_FROM for fact '{Id}' → message '{MsgId}'.",
                        persisted.FactId, msgId);
                    RecordFailureAndMaybeThrow(outcomes, failFast, MemoryItemKind.Fact, IngestionStage.Provenance,
                        MemoryErrorCodes.ProvenancePersistenceFailed, sourceKey, persisted.FactId, ex,
                        $"Ingestion failed fast: provenance failed for fact '{sourceKey}'.");
                }
            }

            await SupersedeReplacedFactsAsync(persisted).ConfigureAwait(false);

            persistedFactCount++;
            _logger.LogDebug("Persisted fact '{S} {P} {O}'.",
                persisted.Subject, persisted.Predicate, persisted.Object);
        }

        // M1 write-time UPDATE. Runs after the write, so the incoming fact is already the winner and a
        // failure here leaves the graph in the append-only state it was in before -- strictly the old
        // behaviour, never a half-resolved one. Best-effort by design: losing a supersession costs
        // precision in live recall, while failing the ingestion over it would lose the memory itself.
        async Task SupersedeReplacedFactsAsync(Fact winner)
        {
            if (!_options.SupersedeReplacedFacts || !WriteTimeFactResolution.CanSupersede(winner))
                return;

            var scope = string.IsNullOrEmpty(ownerId) ? null : MemoryScope.For(ownerId, includeShared: false);
            try
            {
                var losers = await _factRepository.FindSupersededCandidatesAsync(
                    winner.FactId, winner.Subject, winner.Predicate, winner.Object, scope,
                    cancellationToken).ConfigureAwait(false);

                foreach (var loser in losers)
                {
                    await _factRepository.SupersedeAsync(
                        loser.FactId, winner.FactId, scope, cancellationToken).ConfigureAwait(false);
                    _logger.LogDebug(
                        "Superseded fact '{Loser}' with '{Winner}' ({S} {P}).",
                        loser.FactId, winner.FactId, winner.Subject, winner.Predicate);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Write-time supersession failed for fact '{Id}'; it remains stored alongside the "
                    + "assertion it replaces.", winner.FactId);
            }
        }

        async Task PersistFactIndividuallyAsync(Fact item, string sourceKey)
        {
            try
            {
                var persisted = await _factRepository.UpsertAsync(item, cancellationToken).ConfigureAwait(false);
                await RecordPersistedFactAsync(
                    sourceKey, persisted, item.SourceMessageIds).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (MemoryIngestionException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error persisting fact '{Key}'.", sourceKey);
                RecordFailureAndMaybeThrow(outcomes, failFast, MemoryItemKind.Fact, IngestionStage.Persistence,
                    MemoryErrorCodes.FactPersistenceFailed, sourceKey, null, ex,
                    $"Ingestion failed fast: persistence failed for fact '{sourceKey}'.");
            }
        }

        static (string Subject, string Predicate, string Object, string? OwnerId) FactKey(Fact fact) =>
            (fact.Subject, fact.Predicate, fact.Object, fact.OwnerId);

        var distinctExtractedTriples = extraction.FilteredFacts
            .Select(fact => (fact.Subject, fact.Predicate, fact.Object))
            .Distinct(FactTripleComparer.OrdinalIgnoreCase)
            .Count() == extraction.FilteredFacts.Count;
        var fusedFactRepository = _options.UseCoalescedPersistenceTransactions
            ? _factRepository as IFusedBatchMemoryRepository<Fact> : null;
        var batchFactRepository = _factRepository as IBatchMemoryRepository<Fact>;
        var canAttemptFactBatch = _options.EnableBatchMemoryUpserts && !failFast && distinctExtractedTriples &&
            (fusedFactRepository is not null ||
             (prepared.Facts.Count > 1 && batchFactRepository is not null));

        if (canAttemptFactBatch)
        {
            var factInputs = new List<(Fact Item, string SourceKey)>(prepared.Facts.Count);
            foreach (var preparedFact in prepared.Facts)
            {
                if (await PrepareFactAsync(preparedFact).ConfigureAwait(false) is { } input)
                    factInputs.Add(input);
            }

            Dictionary<(string Subject, string Predicate, string Object, string? OwnerId), Fact>? batchedFactsByKey = null;
            if (factInputs.Count > 0 &&
                factInputs.Select(input => FactKey(input.Item)).Distinct().Count() == factInputs.Count)
            {
                try
                {
                    var items = factInputs.Select(input => input.Item).ToList();
                    var persisted = fusedFactRepository is not null
                        ? await fusedFactRepository.UpsertFusedBatchAsync(items, cancellationToken).ConfigureAwait(false)
                        : await batchFactRepository!.UpsertBatchAsync(items, cancellationToken).ConfigureAwait(false);
                    batchedFactsByKey = persisted.ToDictionary(FactKey);
                    if (factInputs.Any(input => !batchedFactsByKey.ContainsKey(FactKey(input.Item))))
                        throw new InvalidOperationException("The fact batch result omitted one or more input triples.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Atomic fact batch failed; replaying {Count} facts through the item path.",
                        factInputs.Count);
                    batchedFactsByKey = null;
                }
            }

            if (batchedFactsByKey is not null)
            {
                foreach (var input in factInputs)
                    await RecordPersistedFactAsync(
                        input.SourceKey, batchedFactsByKey[FactKey(input.Item)],
                        input.Item.SourceMessageIds).ConfigureAwait(false);
            }
            else
            {
                foreach (var input in factInputs)
                    await PersistFactIndividuallyAsync(input.Item, input.SourceKey).ConfigureAwait(false);
            }
        }
        else
        {
            // Preserve the exact original read→write order for non-capable repositories, disabled/fail-fast
            // mode, and duplicate triples. The ordering is observable because the next fact's trust/casing
            // pre-fetch may intentionally see the fact just written by the previous item.
            foreach (var preparedFact in prepared.Facts)
            {
                if (await PrepareFactAsync(preparedFact).ConfigureAwait(false) is { } input)
                    await PersistFactIndividuallyAsync(input.Item, input.SourceKey).ConfigureAwait(false);
            }
        }
        // 3. Embed + upsert preferences.
        var preferenceInputs = prepared.Preferences.Select(preparedPreference =>
        {
            var extracted = preparedPreference.Item;
            return (Item: new Preference
            {
                PreferenceId = _idGenerator.GenerateId(),
                Category = extracted.Category,
                PreferenceText = extracted.PreferenceText,
                Context = extracted.Context,
                Confidence = extracted.Confidence,
                Embedding = preparedPreference.Embedding,
                OwnerId = ownerId,
                SourceMessageIds = SourceTurnProvenance.Resolve(extracted.SourceTurn, sourceMessageIds),
                CreatedAtUtc = _clock.UtcNow,
                Metadata = MemoryTrustMetadataExtensions.CreateWithTrustLevel(
                    SourceRoleTrust.Refine(trustLevel, extracted.SourceRole))
            }, SourceKey: extracted.PreferenceText);
        }).ToList();

        var persistedPrefCount = 0;

        async Task RecordPersistedPreferenceAsync(
            string sourceKey, Preference persisted, IReadOnlyList<string> provenanceMessageIds)
        {
            RecordSuccess(outcomes, MemoryItemKind.Preference, sourceKey, persisted.PreferenceId);

            // The input item's resolved ids, for the same reason the fact path uses them.
            foreach (var msgId in ExplicitProvenanceMessageIds(_preferenceRepository, provenanceMessageIds))
            {
                try
                {
                    await _preferenceRepository.CreateExtractedFromRelationshipAsync(
                        persisted.PreferenceId, msgId, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to create EXTRACTED_FROM for preference '{Id}' → message '{MsgId}'.",
                        persisted.PreferenceId, msgId);
                    RecordFailureAndMaybeThrow(outcomes, failFast, MemoryItemKind.Preference, IngestionStage.Provenance,
                        MemoryErrorCodes.ProvenancePersistenceFailed, sourceKey, persisted.PreferenceId, ex,
                        "Ingestion failed fast: provenance failed for a preference.");
                }
            }

            persistedPrefCount++;
            _logger.LogDebug("Persisted preference in category '{Category}'.", persisted.Category);
        }

        async Task PersistPreferenceIndividuallyAsync(Preference item, string sourceKey)
        {
            try
            {
                var persisted = await _preferenceRepository.UpsertAsync(item, cancellationToken).ConfigureAwait(false);
                await RecordPersistedPreferenceAsync(
                    sourceKey, persisted, item.SourceMessageIds).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (MemoryIngestionException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error persisting preference '{Text}'.", sourceKey);
                RecordFailureAndMaybeThrow(outcomes, failFast, MemoryItemKind.Preference, IngestionStage.Persistence,
                    MemoryErrorCodes.PreferencePersistenceFailed, sourceKey, null, ex,
                    "Ingestion failed fast: persistence failed for a preference.");
            }
        }

        Dictionary<string, Preference>? batchedPreferencesById = null;
        var fusedPreferenceRepository = _options.UseCoalescedPersistenceTransactions
            ? _preferenceRepository as IFusedBatchMemoryRepository<Preference> : null;
        var batchPreferenceRepository = _preferenceRepository as IBatchMemoryRepository<Preference>;
        var canBatchPreferences = _options.EnableBatchMemoryUpserts && !failFast &&
            preferenceInputs.Count > 0 &&
            preferenceInputs.Select(input => input.Item.PreferenceId).Distinct(StringComparer.Ordinal).Count() == preferenceInputs.Count &&
            (fusedPreferenceRepository is not null ||
             (preferenceInputs.Count > 1 && batchPreferenceRepository is not null));
        if (canBatchPreferences)
        {
            try
            {
                var items = preferenceInputs.Select(input => input.Item).ToList();
                var persisted = fusedPreferenceRepository is not null
                    ? await fusedPreferenceRepository.UpsertFusedBatchAsync(items, cancellationToken).ConfigureAwait(false)
                    : await batchPreferenceRepository!.UpsertBatchAsync(items, cancellationToken).ConfigureAwait(false);
                batchedPreferencesById = persisted.ToDictionary(
                    preference => preference.PreferenceId, StringComparer.Ordinal);
                if (preferenceInputs.Any(input => !batchedPreferencesById.ContainsKey(input.Item.PreferenceId)))
                    throw new InvalidOperationException("The preference batch result omitted one or more input identifiers.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Atomic preference batch failed; replaying {Count} preferences through the item path.",
                    preferenceInputs.Count);
                batchedPreferencesById = null;
            }
        }

        if (batchedPreferencesById is not null)
        {
            foreach (var input in preferenceInputs)
                await RecordPersistedPreferenceAsync(
                    input.SourceKey, batchedPreferencesById[input.Item.PreferenceId],
                    input.Item.SourceMessageIds).ConfigureAwait(false);
        }
        else
        {
            foreach (var input in preferenceInputs)
                await PersistPreferenceIndividuallyAsync(input.Item, input.SourceKey).ConfigureAwait(false);
        }
        // 4. Persist relationships — resolve entity IDs from the upserted entity map.
        var relationshipInputs = new List<(Relationship Item, string SourceKey)>(
            extraction.FilteredRelationships.Count);
        foreach (var extracted in extraction.FilteredRelationships)
        {
            var relSourceKey = $"{extracted.SourceEntity}-{extracted.RelationshipType}->{extracted.TargetEntity}";

            if (!persistedEntityMap.TryGetValue(extracted.SourceEntity, out var sourceEntity))
            {
                _logger.LogWarning(
                    "Skipping relationship — source entity '{Source}' was not persisted.",
                    extracted.SourceEntity);
                outcomes.Add(new IngestionItemOutcome
                {
                    Kind = MemoryItemKind.Relationship,
                    Stage = IngestionStage.RelationshipPersistence,
                    Status = IngestionItemStatus.Skipped,
                    SourceKey = relSourceKey,
                    ErrorCode = MemoryErrorCodes.RelationshipEndpointNotPersisted,
                    ErrorMessage = $"Source entity '{extracted.SourceEntity}' was not persisted.",
                });
                continue;
            }

            if (!persistedEntityMap.TryGetValue(extracted.TargetEntity, out var targetEntity))
            {
                _logger.LogWarning(
                    "Skipping relationship — target entity '{Target}' was not persisted.",
                    extracted.TargetEntity);
                outcomes.Add(new IngestionItemOutcome
                {
                    Kind = MemoryItemKind.Relationship,
                    Stage = IngestionStage.RelationshipPersistence,
                    Status = IngestionItemStatus.Skipped,
                    SourceKey = relSourceKey,
                    ErrorCode = MemoryErrorCodes.RelationshipEndpointNotPersisted,
                    ErrorMessage = $"Target entity '{extracted.TargetEntity}' was not persisted.",
                });
                continue;
            }

            relationshipInputs.Add((new Relationship
            {
                RelationshipId = _idGenerator.GenerateId(),
                SourceEntityId = sourceEntity.EntityId,
                TargetEntityId = targetEntity.EntityId,
                RelationshipType = extracted.RelationshipType,
                Description = extracted.Description,
                Confidence = extracted.Confidence,
                Attributes = extracted.Attributes,
                OwnerId = ownerId,
                SourceMessageIds = sourceMessageIds,
                CreatedAtUtc = _clock.UtcNow
            }, relSourceKey));
        }

        var persistedRelCount = 0;

        void RecordPersistedRelationship(string sourceKey, Relationship persisted)
        {
            persistedRelCount++;
            RecordSuccess(outcomes, MemoryItemKind.Relationship, sourceKey, persisted.RelationshipId);
            _logger.LogDebug("Persisted relationship '{SourceKey}'.", sourceKey);
        }

        async Task PersistRelationshipIndividuallyAsync(Relationship item, string sourceKey)
        {
            try
            {
                var persisted = await _relationshipRepository.UpsertAsync(item, cancellationToken).ConfigureAwait(false);
                RecordPersistedRelationship(sourceKey, persisted);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (MemoryIngestionException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error persisting relationship '{SourceKey}'.", sourceKey);
                RecordFailureAndMaybeThrow(outcomes, failFast, MemoryItemKind.Relationship, IngestionStage.Persistence,
                    MemoryErrorCodes.RelationshipPersistenceFailed, sourceKey, null, ex,
                    $"Ingestion failed fast: persistence failed for relationship '{sourceKey}'.");
            }
        }

        Dictionary<string, Relationship>? batchedRelationshipsById = null;
        var canBatchRelationships = _options.EnableBatchMemoryUpserts && !failFast &&
            relationshipInputs.Count > 1 &&
            relationshipInputs.Select(input => input.Item.RelationshipId).Distinct(StringComparer.Ordinal).Count() == relationshipInputs.Count &&
            _relationshipRepository is IBatchMemoryRepository<Relationship>;
        if (canBatchRelationships)
        {
            try
            {
                var persisted = await ((IBatchMemoryRepository<Relationship>)_relationshipRepository)
                    .UpsertBatchAsync(relationshipInputs.Select(input => input.Item).ToList(), cancellationToken)
                    .ConfigureAwait(false);
                batchedRelationshipsById = persisted.ToDictionary(
                    relationship => relationship.RelationshipId, StringComparer.Ordinal);
                if (relationshipInputs.Any(input => !batchedRelationshipsById.ContainsKey(input.Item.RelationshipId)))
                    throw new InvalidOperationException("The relationship batch result omitted one or more input identifiers.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Atomic relationship batch failed; replaying {Count} relationships through the item path.",
                    relationshipInputs.Count);
                batchedRelationshipsById = null;
            }
        }

        if (batchedRelationshipsById is not null)
        {
            foreach (var input in relationshipInputs)
                RecordPersistedRelationship(
                    input.SourceKey, batchedRelationshipsById[input.Item.RelationshipId]);
        }
        else
        {
            foreach (var input in relationshipInputs)
                await PersistRelationshipIndividuallyAsync(input.Item, input.SourceKey).ConfigureAwait(false);
        }
        return new PersistenceResult
        {
            EntityCount = persistedEntityMap.Count,
            FactCount = persistedFactCount,
            PreferenceCount = persistedPrefCount,
            RelationshipCount = persistedRelCount,
            Outcomes = outcomes
        };
    }

    private async Task<PreparedEmbeddings> PrepareEmbeddingsIndividuallyAsync(
        ExtractionStageResult extraction,
        CancellationToken cancellationToken)
    {
        var failFast = _options.FailureMode == IngestionFailureMode.FailFast;
        var outcomes = new List<IngestionItemOutcome>();
        var entities = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
        var facts = new List<PreparedFact>(extraction.FilteredFacts.Count);
        var preferences = new List<PreparedPreference>(extraction.FilteredPreferences.Count);

        foreach (var (name, entity) in extraction.ResolvedEntityMap)
        {
            if (entity.Embedding is not null)
            {
                entities[name] = entity;
                continue;
            }

            try
            {
                var embedding = await _embeddingOrchestrator.EmbedEntityAsync(
                    entity.Name, cancellationToken).ConfigureAwait(false);
                entities[name] = entity with { Embedding = embedding };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating embedding for entity '{Name}'.", name);
                RecordFailureAndMaybeThrow(outcomes, failFast, MemoryItemKind.Entity, IngestionStage.Embedding,
                    MemoryErrorCodes.EmbeddingGenerationFailed, name, null, ex,
                    $"Ingestion failed fast: embedding generation failed for entity '{name}'.");
            }
        }

        foreach (var extracted in extraction.FilteredFacts)
        {
            var sourceKey = $"{extracted.Subject} {extracted.Predicate} {extracted.Object}";
            try
            {
                var embedding = await _embeddingOrchestrator.EmbedFactAsync(
                    extracted.Subject, extracted.Predicate, extracted.Object, cancellationToken).ConfigureAwait(false);
                facts.Add(new PreparedFact(extracted, embedding));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating embedding for fact '{Key}'.", sourceKey);
                RecordFailureAndMaybeThrow(outcomes, failFast, MemoryItemKind.Fact, IngestionStage.Embedding,
                    MemoryErrorCodes.EmbeddingGenerationFailed, sourceKey, null, ex,
                    $"Ingestion failed fast: embedding generation failed for fact '{sourceKey}'.");
            }
        }

        foreach (var extracted in extraction.FilteredPreferences)
        {
            try
            {
                var embedding = await _embeddingOrchestrator.EmbedPreferenceAsync(
                    extracted.PreferenceText, cancellationToken).ConfigureAwait(false);
                preferences.Add(new PreparedPreference(extracted, embedding));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating embedding for preference '{Text}'.", extracted.PreferenceText);
                RecordFailureAndMaybeThrow(outcomes, failFast, MemoryItemKind.Preference, IngestionStage.Embedding,
                    MemoryErrorCodes.EmbeddingGenerationFailed, extracted.PreferenceText, null, ex,
                    "Ingestion failed fast: embedding generation failed for a preference.");
            }
        }

        return new PreparedEmbeddings(entities, facts, preferences, outcomes);
    }

    private sealed class FactTripleComparer : IEqualityComparer<(string Subject, string Predicate, string Object)>
    {
        public static FactTripleComparer OrdinalIgnoreCase { get; } = new();

        public bool Equals(
            (string Subject, string Predicate, string Object) left,
            (string Subject, string Predicate, string Object) right) =>
            StringComparer.OrdinalIgnoreCase.Equals(left.Subject, right.Subject) &&
            StringComparer.OrdinalIgnoreCase.Equals(left.Predicate, right.Predicate) &&
            StringComparer.OrdinalIgnoreCase.Equals(left.Object, right.Object);

        public int GetHashCode((string Subject, string Predicate, string Object) value) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Subject),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Predicate),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Object));
    }
    private sealed record PreparedEmbeddings(
        IReadOnlyDictionary<string, Entity> Entities,
        IReadOnlyList<PreparedFact> Facts,
        IReadOnlyList<PreparedPreference> Preferences,
        IReadOnlyList<IngestionItemOutcome> Outcomes);

    private sealed record PreparedFact(ExtractedFact Item, float[] Embedding);

    private sealed record PreparedPreference(ExtractedPreference Item, float[] Embedding);

    /// <summary>
    /// Trust is monotonic (#92 Phase 3): re-touching an already-persisted entity must never silently lower
    /// its trust level below whatever it already had.
    /// </summary>
    private static MemoryTrustLevel MaxTrustLevel(MemoryTrustLevel a, MemoryTrustLevel b) => a > b ? a : b;

    private static IEnumerable<string> ExplicitProvenanceMessageIds(
        object repository, IReadOnlyList<string> sourceMessageIds) =>
        repository is IUpsertPersistsProvenance ? Array.Empty<string>() : sourceMessageIds;

    /// <summary>Appends a <see cref="IngestionItemStatus.Succeeded"/> outcome (#101).</summary>
    private static void RecordSuccess(
        List<IngestionItemOutcome> outcomes, MemoryItemKind kind, string? sourceKey, string? persistedId) =>
        outcomes.Add(new IngestionItemOutcome
        {
            Kind = kind,
            Stage = IngestionStage.Persistence,
            Status = IngestionItemStatus.Succeeded,
            SourceKey = sourceKey,
            PersistedId = persistedId,
        });

    /// <summary>
    /// Appends a <see cref="IngestionItemStatus.Failed"/> outcome and, under
    /// <see cref="IngestionFailureMode.FailFast"/>, throws <see cref="MemoryIngestionException"/>
    /// carrying every outcome recorded so far (#101). Centralizing this in one helper — rather than
    /// repeating "add outcome, then maybe throw" at each of the ~10 call sites across four item kinds —
    /// is what guarantees every one of them gets the same fail-fast behavior; hand-copying it previously
    /// let the relationship block silently miss the failure-mode check entirely (caught in review).
    /// </summary>
    private static void RecordFailureAndMaybeThrow(
        List<IngestionItemOutcome> outcomes,
        bool failFast,
        MemoryItemKind kind,
        IngestionStage stage,
        string errorCode,
        string? sourceKey,
        string? persistedId,
        Exception ex,
        string failFastMessage)
    {
        outcomes.Add(new IngestionItemOutcome
        {
            Kind = kind,
            Stage = stage,
            Status = IngestionItemStatus.Failed,
            SourceKey = sourceKey,
            PersistedId = persistedId,
            ErrorCode = errorCode,
            ErrorMessage = ex.Message,
            Retryable = true,
        });

        if (failFast)
            throw new MemoryIngestionException(failFastMessage, outcomes, ex);
    }
}
