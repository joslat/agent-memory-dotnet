using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Memory;
using AgentMemory.Core.Services;

namespace AgentMemory.Core.Extraction;

/// <summary>
/// Coordinates embedding preparation, typed writes and their consequences.
/// Owns the storage transaction and confirmed-rollback replay boundaries.
/// </summary>
internal sealed class PersistenceStage : IPersistenceStage
{
    private readonly IClock _clock;
    private readonly ExtractionOptions _options;
    private readonly IMemoryPersistenceTransaction _persistenceTransaction;
    private readonly ILogger<PersistenceStage> _logger;
    private readonly WorkingMemoryRebuilder _rebuilder;
    private readonly ConfirmedMemoryReinforcement _reinforcement;
    private readonly MemoryEmbeddingPreparation _embeddingPreparation;
    private readonly EntityPersistence _entityPersistence;
    private readonly RelationshipPersistence _relationshipPersistence;
    private readonly PreferencePersistence _preferencePersistence;
    private readonly FactPersistence _factPersistence;
    private readonly JudgedMemoryClosings _judgedClosings;
    private readonly CorrectedNamePropagation _namePropagation;
    private readonly EdgeEnding _edgeEnding;

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
        IOptions<MemoryOptions>? memoryOptions = null,
        // 41.06. Optional and last for the same reason: without a judge the write path is exactly what it was.
        IMemoryUpdateJudge? updateJudge = null,
        // AMWRITE001, 41.26 (c). Optional and last: asked for the writer's named closings only when the judge above is
        // missing, off or fails.
        IMemoryUpdateJudgeFallback? fallbackJudge = null)
    {
        _reinforcement = new ConfirmedMemoryReinforcement(factRepository, preferenceRepository,
            memoryOptions?.Value.ConfidenceReinforcementAlpha ?? 0.0, logger);
        _clock = clock;
        _logger = logger;
        _persistenceTransaction = persistenceTransaction ?? throw new ArgumentNullException(nameof(persistenceTransaction));
        _options = extractionOptions?.Value ?? new ExtractionOptions();
        _embeddingPreparation = new MemoryEmbeddingPreparation(embeddingOrchestrator, _options, logger);
        _entityPersistence = new EntityPersistence(entityRepository, factRepository, embeddingOrchestrator, _options, logger);
        var closings = new ClosingPipeline(factRepository, preferenceRepository, relationshipRepository, entityRepository, _options);
        _relationshipPersistence = new RelationshipPersistence(entityRepository, relationshipRepository, clock, idGenerator, _options, logger, closings);
        _preferencePersistence = new PreferencePersistence(preferenceRepository, clock, idGenerator, _options, logger, closings);
        var factPreparation = new FactPreparation(factRepository, idGenerator, clock, logger);
        _factPersistence = new FactPersistence(factRepository, clock, _options, logger, factPreparation, closings);
        _judgedClosings = new JudgedMemoryClosings(embeddingOrchestrator, entityRepository, factRepository,
            preferenceRepository, relationshipRepository, clock, _options, logger,
            new ClosingPolicy(_options, updateJudge, fallbackJudge, logger), closings);
        _namePropagation = new CorrectedNamePropagation(entityRepository, factRepository, embeddingOrchestrator,
            clock, idGenerator, _options, logger, closings);
        _edgeEnding = new EdgeEnding(entityRepository, factRepository, relationshipRepository, clock, _options, logger, closings);
        // 40.65. The same "on and inert" signal supersession gives: the option only says how a closing is recorded.
        if (_options.BitemporalChanges && !_options.SupersedeReplacedFacts && Interlocked.Exchange(ref s_warnedInertBitemporal, 1) == 0)
            _logger.LogWarning(
                "ExtractionOptions.BitemporalChanges is ENABLED but SupersedeReplacedFacts is off, so nothing is closed and "
                + "the option is on and inert. Turn SupersedeReplacedFacts on for changes and corrections to be recorded.");
        _rebuilder = new WorkingMemoryRebuilder(
            workingMemory,
            memoryOptions?.Value.WorkingMemory ?? new WorkingMemoryOptions(),
            _logger);
    }

    private static int s_warnedInertBitemporal;

    public async Task<PersistenceResult> PersistAsync(
        ExtractionStageResult extraction,
        string? ownerId = null,
        MemoryTrustLevel trustLevel = MemoryTrustLevel.Untrusted,
        CancellationToken cancellationToken = default)
    {
        var started = _clock.UtcNow;
        var result = await PersistCoreAsync(extraction, ownerId, trustLevel, cancellationToken)
            .ConfigureAwait(false);

        // J-11. After the write (whichever path produced it), outside its transaction: a name that replaced another
        // renames what it named. Before the rebuild below, so the working-memory block is compiled from the result.
        await _namePropagation.ApplyAsync(extraction, ownerId, started, cancellationToken).ConfigureAwait(false);

        // AMWRITE001. What the writer said the turn says again is counted again, as a repeated write is; nothing is rewritten.
        await _reinforcement.ApplyAsync(extraction, ownerId, cancellationToken).ConfigureAwait(false);

        // Once per persist, and here rather than inside the core so it happens exactly once whichever
        // of the three return paths (atomic / best-effort / replay) produced the result, and outside
        // the storage transaction either way. A throw from the core skips it, which is right: nothing
        // was persisted, so there is nothing to recompile.
        await RebuildWorkingMemoryAsync(ownerId, result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Rebuilds working memory once after successful core persistence and post-write consequences.
    /// Runs outside the transaction; the rebuilder handles best-effort failure without undoing stored memories.
    /// </summary>
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

        // 37.2. The object written twice ("Daniel | is a chef | chef") is written once. Always: it is how the triple
        // reads, not a feature.
        if (extraction.FilteredFacts.Count > 0)
            extraction = extraction with { FilteredFacts = [.. extraction.FilteredFacts.Select(PredicateEcho.Trim)] };

        // 36.4. A change of mind in the shape supersession can act on: ages as the single-valued `age`, and the
        // state an event entails ("moved to" -> "lives in") beside it. Only with supersession, the feature it serves.
        if (_options.SupersedeReplacedFacts && !extraction.WrittenByWriter && extraction.FilteredFacts.Count > 0)
            extraction = extraction with
            {
                FilteredFacts = ReplacementShapes.Prepare(
                    extraction.FilteredFacts,
                    name => extraction.ResolvedEntityMap.TryGetValue(name, out var entity) ? entity.Type : null,
                    _clock.UtcNow),
            };

        var prepared = await _embeddingPreparation.PrepareAsync(extraction, cancellationToken).ConfigureAwait(false);
        if (_options.DeduplicateWithinExtraction && prepared.Facts.Count > 1)
        {
            var (kept, merged) = WithinExtractionFactDeduplication.Merge(prepared.Facts, _options.WithinExtractionDuplicateThreshold);
            prepared = prepared with { Facts = kept, Outcomes = [.. prepared.Outcomes, .. merged] };
        }
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
        var closings = new List<(string Winner, string Loser)>();
        PersistenceClosingJournal.Begin(closings);

        // 1. Resolve the speaker, then persist entities for fact and relationship resolution.
        // I-5. "user" is the owner: once the user has said their name, what they say about themselves is
        // stored under it. Read only when this extraction has something to rewrite.
        var userName = _options.ResolveUserToName && !string.IsNullOrWhiteSpace(ownerId) &&
                       (prepared.Facts.Any(f => MemoryUserNames.MeansUser(f.Item, subject: true) || MemoryUserNames.MeansUser(f.Item, subject: false)) ||
                        prepared.Entities.Keys.Any(MemoryUserNames.IsSelf) ||
                        extraction.FilteredRelationships.Any(r => MemoryUserNames.MeansUserEndpoint(r.SourceEntity, source: true) ||
                                                                  MemoryUserNames.MeansUserEndpoint(r.TargetEntity, source: false)))
            ? await _entityPersistence.FindUserNameAsync(prepared.Facts, ownerId!, cancellationToken).ConfigureAwait(false)
            : null;

        var persistedEntities = await _entityPersistence.PersistAsync(
            prepared.Entities, userName, ownerId, trustLevel, sourceMessageIds, outcomes, failFast, cancellationToken)
            .ConfigureAwait(false);
        var persistedEntityMap = persistedEntities.ByName;
        var persistedFacts = await _factPersistence.PersistAsync(
            prepared.Facts, persistedEntityMap, userName, ownerId, trustLevel, sourceMessageIds,
            outcomes, failFast, extraction.WrittenByWriter, cancellationToken).ConfigureAwait(false);
        var factsByKey = persistedFacts.BySourceKey;
        var persistedPreferences = await _preferencePersistence.PersistAsync(
            prepared.Preferences, ownerId, trustLevel, sourceMessageIds, outcomes, failFast,
            extraction.WrittenByWriter, cancellationToken).ConfigureAwait(false);
        var preferencesByKey = persistedPreferences.BySourceKey;
        var persistedRelationships = await _relationshipPersistence.PersistAsync(
            extraction.FilteredRelationships, persistedEntityMap, userName, ownerId, sourceMessageIds,
            outcomes, failFast, extraction.WrittenByWriter, cancellationToken).ConfigureAwait(false);
        await _edgeEnding.ApplyAsync(persistedFacts.Replacements.Withdrawn, cancellationToken).ConfigureAwait(false);

        // The batch-level signal. Debug-level per-fact logging tells you WHY once you are already
        // looking; this is what makes you look. Warned once per batch rather than per fact so a large
        // ingestion cannot bury it, and only when the option was actually asked for -- a warning on a
        // feature nobody enabled is how warnings stop being read.
        if (_options.SupersedeReplacedFacts && !extraction.WrittenByWriter && persistedFacts.Replacements.Eligible == 0 && persistedFacts.Replacements.Refusals > 0)
        {
            _logger.LogWarning(
                "SupersedeReplacedFacts is ENABLED but none of the {Refused} fact(s) in this batch "
                + "had a predicate declared single-valued, so NO supersession was attempted and no "
                + ":SUPERSEDED_BY edge was written. The feature is on and inert for this content. "
                + "Single-valued relations: {Known}. Either the extracted predicates need to "
                + "canonicalise into that set, or supersession does not apply to this material.",
                persistedFacts.Replacements.Refusals,
                string.Join(", ", MemoryRelationCardinality.SingleValuedPredicates));
        }

        // AMWRITE001. A store-aware writer named what each new memory replaces: only those pairs go to the judge.
        if (extraction.WrittenByWriter)
            await _judgedClosings.CloseNamedAsync(extraction, ownerId, [.. prepared.Facts.Select(f => f.Item)], [.. prepared.Preferences.Select(p => p.Item)],
                factsByKey, preferencesByKey,
                persistedRelationships.Inputs.Where(input => persistedRelationships.PersistedIds.Contains(input.Item.RelationshipId))
                    .GroupBy(input => input.SourceKey, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First().Item.RelationshipId, StringComparer.Ordinal),
                cancellationToken).ConfigureAwait(false);
        // 41.06. A change said in other words closes what it replaces, when a judge is registered.
        else if (_judgedClosings.IsEnabled)
            await _judgedClosings.JudgeUpdatesAsync(
                extraction, ownerId,
                [.. factsByKey.Values.Where(fact => persistedFacts.CreatedIds.Contains(fact.FactId)).DistinctBy(fact => fact.FactId)],
                [.. preferencesByKey.Values.Where(p => persistedPreferences.CreatedIds.Contains(p.PreferenceId)).DistinctBy(p => p.PreferenceId)],
                cancellationToken).ConfigureAwait(false);

        // G4. Each fact (or, through the update judge, preference) that closed others says which.
        if (closings.Count > 0)
        {
            var closedBy = closings.GroupBy(c => c.Winner, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(c => c.Loser).Distinct(StringComparer.Ordinal)], StringComparer.Ordinal);
            for (var i = 0; i < outcomes.Count; i++)
            {
                if (outcomes[i] is { Kind: MemoryItemKind.Fact or MemoryItemKind.Preference, Status: IngestionItemStatus.Succeeded, PersistedId: { } id } &&
                    closedBy.TryGetValue(id, out var losers))
                    outcomes[i] = outcomes[i] with { Closed = losers };
            }
        }

        return new PersistenceResult
        {
            EntityCount = persistedEntities.Count,
            FactCount = persistedFacts.Count,
            PreferenceCount = persistedPreferences.Count,
            RelationshipCount = persistedRelationships.Count,
            Outcomes = outcomes
        };
    }
}
