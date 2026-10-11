using static AgentMemory.Core.Extraction.PersistenceMetadata;
using static AgentMemory.Core.Extraction.PersistenceOutcomes;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Memory;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Core.Extraction;

/// <summary>Persists facts with the existing batch/replay order, provenance and entity links.</summary>
internal sealed class FactPersistence
{
    private readonly IFactRepository _factRepository;
    private readonly IClock _clock;
    private readonly ExtractionOptions _options;
    private readonly ILogger _logger;
    private readonly FactPreparation _factPreparation;
    private readonly FactReplacementResolver _replacements;

    public FactPersistence(IFactRepository factRepository, IClock clock, ExtractionOptions options,
        ILogger logger, FactPreparation factPreparation, FactClosureWriter factClosures)
    {
        _factRepository = factRepository;
        _clock = clock;
        _options = options;
        _logger = logger;
        _factPreparation = factPreparation;
        _replacements = new FactReplacementResolver(factRepository, options, logger, factClosures);
    }

    public async Task<FactPersistenceResult> PersistAsync(IReadOnlyList<PreparedFact> facts,
        IReadOnlyDictionary<string, Entity> persistedEntityMap, string? userName, string? ownerId,
        MemoryTrustLevel trustLevel, IReadOnlyList<string> sourceMessageIds, List<IngestionItemOutcome> outcomes,
        bool failFast, bool writtenByWriter, CancellationToken cancellationToken = default)
    {
        var factNames = new FactNames(_options, persistedEntityMap, userName);
        var persistedFactCount = 0;
        var state = new FactWriteSet(facts, factNames, _options, writtenByWriter, _clock);
        var createdHere = state.CreatedIds;
        var extractedByKey = state.ExtractedByKey;
        var writtenFacts = state.WrittenFacts;

        Task<(Fact Item, string SourceKey)?> PrepareFactAsync(PreparedFact fact) =>
            _factPreparation.PrepareAsync(fact, factNames, ownerId, trustLevel, sourceMessageIds,
                outcomes, failFast, cancellationToken);

        async Task LinkFactToNamedEntitiesAsync(Fact persisted)
        {
            // Subject and object are checked separately and deduplicated: a fact like
            // "Rome | is_capital_of | Rome" would otherwise write the same edge twice, and MERGE
            // would absorb it silently rather than showing the caller was confused.
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(persisted.Subject)) names.Add(persisted.Subject);
            if (!string.IsNullOrWhiteSpace(persisted.Object)) names.Add(persisted.Object);

            foreach (var name in names)
            {
                if (!persistedEntityMap.TryGetValue(name, out var entity)) continue;

                try
                {
                    await _factRepository
                        .CreateAboutRelationshipAsync(persisted.FactId, entity.EntityId, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // A missing ABOUT edge degrades retrieval; a thrown one loses the fact that was
                    // already persisted. The edge is an enrichment of a write that has succeeded, so
                    // it must never be the reason the write is reported as failed.
                    _logger.LogWarning(
                        ex, "Could not link fact {FactId} to entity {EntityId} ('{Name}').",
                        persisted.FactId, entity.EntityId, name);
                }
            }
        }

        async Task RecordPersistedFactAsync(
            string sourceKey, Fact persisted, IReadOnlyList<string> provenanceMessageIds)
        {
            // Fact upsert MERGEs on the natural triple and may return an older stable id. Always use
            // the repository result for outcomes and provenance rather than the fresh caller id.
            RecordSuccess(outcomes, MemoryItemKind.Fact, sourceKey, persisted.FactId,
                createdHere.Contains(persisted.FactId) ? MemoryWriteEffect.Created : MemoryWriteEffect.AlreadyStored);
            state.RecordWritten(sourceKey, extractedByKey.GetValueOrDefault(sourceKey), persisted);

            // Use this extraction's entity lookup, including captured aliases, for subject/object links.
            // This does not issue another entity-resolution search. A failed link does not fail the stored fact.
            if (_options.LinkFactsToEntities)
            {
                await LinkFactToNamedEntitiesAsync(persisted).ConfigureAwait(false);
            }

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

            writtenFacts.Add((sourceKey, persisted));

            persistedFactCount++;
            _logger.LogDebug("Persisted fact '{S} {P} {O}'.",
                persisted.Subject, persisted.Predicate, persisted.Object);
        }

        async Task PersistFactIndividuallyAsync(Fact item, string sourceKey)
        {
            try
            {
                var persisted = await _factRepository.UpsertAsync(item, cancellationToken).ConfigureAwait(false);
                if (persisted.FactId == item.FactId) createdHere.Add(persisted.FactId);
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

        // Distinct as STORED: canonical names applied, compared on the storage MERGE key (values by
        // CanonicalValue, the predicate by Canonical, which folds "_" and "-": "works_at" and "works at"
        // are one key, exactly as Neo4jFactRepository computes it). Two facts that the
        // extraction phrased apart but that land on one node ("Tomás | works at | Acme" and "Tomás Silva |
        // Works at | Acme" under canonical subjects) must take the sequential path, where the second one's
        // pre-fetch sees the first; batched, the MERGE folds them and the batch replays both (review round 2).
        var distinctExtractedTriples = facts
            .Select(fact => (
                MemoryTripleCanonicalizer.CanonicalValue(factNames.StoredName(fact.Item, subject: true)),
                MemoryTripleCanonicalizer.Canonical(fact.Item.Predicate),
                MemoryTripleCanonicalizer.CanonicalValue(factNames.StoredName(fact.Item, subject: false))))
            .Distinct()
            .Count() == facts.Count;
        var fusedFactRepository = _options.UseCoalescedPersistenceTransactions
            ? _factRepository as IFusedBatchMemoryRepository<Fact> : null;
        var batchFactRepository = _factRepository as IBatchMemoryRepository<Fact>;
        var canAttemptFactBatch = _options.EnableBatchMemoryUpserts && !failFast && distinctExtractedTriples &&
            (fusedFactRepository is not null ||
             (facts.Count > 1 && batchFactRepository is not null));

        if (canAttemptFactBatch)
        {
            var factInputs = new List<(Fact Item, string SourceKey)>(facts.Count);
            foreach (var preparedFact in facts)
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
                // Every fact of the batch is already stored: what it created, and which fact is which, is known before
                // any correction runs.
                foreach (var input in factInputs)
                {
                    var written = batchedFactsByKey[FactKey(input.Item)];
                    if (written.FactId == input.Item.FactId) createdHere.Add(input.Item.FactId);
                    state.RecordWritten(input.SourceKey, extractedByKey.GetValueOrDefault(input.SourceKey), written);
                }
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
            foreach (var preparedFact in facts)
            {
                if (await PrepareFactAsync(preparedFact).ConfigureAwait(false) is { } input)
                    await PersistFactIndividuallyAsync(input.Item, input.SourceKey).ConfigureAwait(false);
            }
        }
        var replacements = _options.SupersedeReplacedFacts && !writtenByWriter
            ? await _replacements.ApplyAsync(state, ownerId, writtenByWriter, cancellationToken).ConfigureAwait(false)
            : FactReplacementResult.Empty;
        return new FactPersistenceResult
        {
            BySourceKey = state.ByKey,
            CreatedIds = state.CreatedIds,
            Count = persistedFactCount,
            Replacements = replacements,
        };
    }
}

internal sealed record FactPersistenceResult
{
    public required IReadOnlyDictionary<string, Fact> BySourceKey { get; init; }
    public required IReadOnlySet<string> CreatedIds { get; init; }
    public required int Count { get; init; }
    public required FactReplacementResult Replacements { get; init; }
}
