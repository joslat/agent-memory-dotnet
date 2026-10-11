using static AgentMemory.Core.Extraction.FactClosureWriter;
using static AgentMemory.Core.Extraction.FactNames;
using static AgentMemory.Core.Extraction.PersistenceMetadata;
using static AgentMemory.Core.Extraction.PersistenceOutcomes;
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
/// Embeds and persists the resolved items from <see cref="ExtractionStage"/>.
/// Responsibility: generate embeddings, upsert to repositories, wire EXTRACTED_FROM provenance.
/// </summary>
internal sealed class PersistenceStage : IPersistenceStage
{
    private readonly IFactRepository _factRepository;
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
    private readonly FactPreparation _factPreparation;
    private readonly FactClosureWriter _factClosures;
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
        _factRepository = factRepository;
        _clock = clock;
        _logger = logger;
        _persistenceTransaction = persistenceTransaction ?? throw new ArgumentNullException(nameof(persistenceTransaction));
        _options = extractionOptions?.Value ?? new ExtractionOptions();
        _embeddingPreparation = new MemoryEmbeddingPreparation(embeddingOrchestrator, _options, logger);
        _entityPersistence = new EntityPersistence(entityRepository, embeddingOrchestrator, _options, logger);
        _relationshipPersistence = new RelationshipPersistence(entityRepository, relationshipRepository, clock, idGenerator, _options, logger);
        _preferencePersistence = new PreferencePersistence(preferenceRepository, clock, idGenerator, _options, logger);
        _factPreparation = new FactPreparation(factRepository, idGenerator, clock, logger);
        _factClosures = new FactClosureWriter(factRepository, _options);
        _judgedClosings = new JudgedMemoryClosings(embeddingOrchestrator, entityRepository, factRepository,
            preferenceRepository, relationshipRepository, clock, _options, logger, updateJudge, fallbackJudge, _factClosures);
        _namePropagation = new CorrectedNamePropagation(entityRepository, factRepository, embeddingOrchestrator,
            clock, idGenerator, _options, logger, _factClosures);
        _edgeEnding = new EdgeEnding(entityRepository, factRepository, relationshipRepository, clock, _options, logger, _factClosures);
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

    /// <summary>
    /// AMWRITE001. True for the persist of a turn a store-aware writer decided (<see cref="ExtractionStageResult.WrittenByWriter"/>):
    /// set at the start of <see cref="PersistAsync"/>, so every await inside it sees it and none outside it does.
    /// </summary>
    private static readonly AsyncLocal<bool> s_writerTurn = new();

    /// <summary>
    /// <see cref="ExtractionOptions.SupersedeReplacedFacts"/> for this persist: off for a writer's turn, whose closings are only
    /// the ones it named, each confirmed by the update judge (the measured form had no other closer).
    /// </summary>
    private bool SupersedeReplacedFacts => _options.SupersedeReplacedFacts && !s_writerTurn.Value;

    public async Task<PersistenceResult> PersistAsync(
        ExtractionStageResult extraction,
        string? ownerId = null,
        MemoryTrustLevel trustLevel = MemoryTrustLevel.Untrusted,
        CancellationToken cancellationToken = default)
    {
        var started = _clock.UtcNow;
        s_writerTurn.Value = extraction.WrittenByWriter;
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

        // 37.2. The object written twice ("Daniel | is a chef | chef") is written once. Always: it is how the triple
        // reads, not a feature.
        if (extraction.FilteredFacts.Count > 0)
            extraction = extraction with { FilteredFacts = [.. extraction.FilteredFacts.Select(PredicateEcho.Trim)] };

        // 36.4. A change of mind in the shape supersession can act on: ages as the single-valued `age`, and the
        // state an event entails ("moved to" -> "lives in") beside it. Only with supersession, the feature it serves.
        if (SupersedeReplacedFacts && extraction.FilteredFacts.Count > 0)
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
            ? await UserNameAsync(prepared.Facts, ownerId!, cancellationToken).ConfigureAwait(false)
            : null;

        var persistedEntities = await _entityPersistence.PersistAsync(
            prepared.Entities, userName, ownerId, trustLevel, sourceMessageIds, outcomes, failFast, cancellationToken)
            .ConfigureAwait(false);
        var persistedEntityMap = persistedEntities.ByName;
        // 2. Embed + upsert facts.
        var factNames = new FactNames(_options, persistedEntityMap, userName);
        var persistedFactCount = 0;
        // 36.4. What each marked correction replaces, by the words it was extracted as (the source key every
        // outcome of this persist is keyed by). Empty unless the extractor was asked to mark corrections. A correction
        // naming its own value marks nothing: the extractor repeated the value, and the fact is an ordinary statement.
        var factCorrections = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var preparedFact in prepared.Facts)
        {
            var item = preparedFact.Item;
            if (string.IsNullOrWhiteSpace(item.Replaces)) continue;
            var value = Corrections.Value(factNames.CanonicalName(item.Replaces!));
            if (value.Length == 0 || value == Corrections.Value(factNames.StoredName(item, subject: false))) continue;
            factCorrections.TryAdd(FactSourceKey(item), item.Replaces!);
        }
        // Which value this extraction leaves current (CurrentValues), in stored terms: the subject as stored (the self
        // words one speaker), the value as stored. A replaced fact supersedes nothing; its current value closes it once
        // every fact is written, on either write path.
        string FactSubjectKey(ExtractedFact fact)
        {
            var stored = factNames.StoredName(fact, subject: true);
            return MemoryUserNames.IsSelf(stored) ? "\u0001self" : MemoryTripleCanonicalizer.CanonicalValue(stored);
        }
        // A value that has ended, or not begun, states no current value ("lived in Berlin until 2019", "moving to Oslo
        // next month") and joins no group: one rule (ReplacementShapes.HoldsNow) with the supersession query's.
        var now = _clock.UtcNow;
        string? FactRelationKey(ExtractedFact fact) =>
            ReplacementShapes.HoldsNow(fact.ValidFrom ?? fact.OccurredOn, fact.ValidUntil, now) &&
            MemoryRelationCardinality.Relation(fact.Predicate) is { } relation
                ? FactSubjectKey(fact) + "\u0001" + relation
                : null;
        var factDecision = !SupersedeReplacedFacts
            ? new CurrentValues.Decision(new Dictionary<string, string>(), new HashSet<string>())
            : CurrentValues.Decide(
                prepared.Facts.Select(f => f.Item).ToList(),
                FactSourceKey,
                FactRelationKey,
                (fact, correction) =>
                    factCorrections.TryGetValue(FactSourceKey(correction), out var named) &&
                    FactSubjectKey(fact) == FactSubjectKey(correction) &&
                    MemoryRelationCardinality.ReplacedKeys(correction.Predicate)
                        .Contains(MemoryTripleCanonicalizer.Canonical(fact.Predicate), StringComparer.Ordinal) &&
                    Corrections.Value(factNames.StoredName(fact, subject: false)) == Corrections.Value(factNames.CanonicalName(named)),
                fact => fact.ValidFrom ?? fact.OccurredOn ?? now);
        var replacedFacts = factDecision.Replaced;
        // The order the facts were said, by source key, and each written fact by its key.
        var factIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (preparedFact, index) in prepared.Facts.Select((f, i) => (f, i)))
            factIndex.TryAdd(FactSourceKey(preparedFact.Item), index);
        var factsByKey = new Dictionary<string, Fact>(StringComparer.Ordinal);
        // Every fact written, in the order recorded: nothing is closed until all of them are (review round 8: the item
        // path closed while writing, so it saw a value not yet restated as absent, and the paths disagreed).
        var writtenFacts = new List<(string Key, Fact Fact)>();
        // The current value of each single-valued relation this extraction states: no correction of the same extraction
        // closes it ("Copenhagen, not Oslo ... no, Oslo, not Copenhagen" leaves Oslo).
        var currentFactIds = new HashSet<string>(StringComparer.Ordinal);
        void Written(string sourceKey, ExtractedFact? extracted, Fact persisted)
        {
            factsByKey.TryAdd(sourceKey, persisted);
            if (!replacedFacts.ContainsKey(sourceKey) && extracted is not null && FactRelationKey(extracted) is not null)
                currentFactIds.Add(persisted.FactId);
        }
        var extractedByKey = new Dictionary<string, ExtractedFact>(StringComparer.Ordinal);
        foreach (var preparedFact in prepared.Facts)
            extractedByKey.TryAdd(FactSourceKey(preparedFact.Item), preparedFact.Item);
        // The facts this extraction CREATED: a correction's other-relation fallback leaves them alone ("Oslo now, not
        // Copenhagen; Copenhagen is still my favourite city"). A stored fact merely restated is not among them: the
        // fallback exists for what was stored before.
        var createdHere = new HashSet<string>(StringComparer.Ordinal);
        // Counted so the batch can say whether the lever did anything at all -- the "you turned this
        // on and it was inert" signal that took a week and four scored runs to notice its absence.
        var supersessionEligible = 0;
        // 38.6 review: what each closed fact also said elsewhere (its edge, its date) ends after this extraction's own
        // relationships are written; ended earlier, the same extraction could write the closed fact's edge again, live.
        var withdrawn = new List<WithdrawnFact>();
        var supersessionRefusals = 0;

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
            Written(sourceKey, extractedByKey.GetValueOrDefault(sourceKey), persisted);

            // W-E1. Link the fact to the entities its subject or object NAMES. Extraction has always
            // persisted both and never connected them: CreateAboutRelationshipAsync is public,
            // unit-tested and proven against live Neo4j, and no ingestion path has ever called it.
            // Every store probe this project has run reports 0 entity(ies), on every line.
            //
            // Matching is by name only, deliberately. This does NOT resolve aliases -- "head office"
            // and "the Calderwick office" stay two names until something declares them one. It is the
            // substrate alias resolution needs, not alias resolution, and calling it the latter would
            // repeat a half-wired-feature mistake this codebase has already paid for twice.
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

        // M1 write-time UPDATE. Runs after the write, so the incoming fact is already the winner and a
        // failure here leaves the graph in the append-only state it was in before -- strictly the old
        // behaviour, never a half-resolved one. Best-effort by design: losing a supersession costs
        // precision in live recall, while failing the ingestion over it would lose the memory itself.
        // With <under>, the winner supersedes what the replaced fact <under> would have: the same guards, one routine.
        async Task SupersedeReplacedFactsAsync(Fact winner, Fact? under = null)
        {
            if (!SupersedeReplacedFacts) return;
            var said = under ?? winner;

            // The silent no-op this closes. `CanSupersede` requires the predicate to be one of the
            // relations the vocabulary declares single-valued, and it is FALSE for anything
            // unrecognised -- so a caller who enables SupersedeReplacedFacts against free-form
            // predicates ("was at", "assigned to", "department") gets a feature that is on and inert,
            // writes no :SUPERSEDED_BY edge, and says nothing about it anywhere.
            //
            // That is not hypothetical: a benchmark ran four scored arms against it before the cause
            // was found, and only because the graph could be queried directly. A consumer has no such
            // recourse, so the refusal is now observable.
            if (!WriteTimeFactResolution.CanSupersede(said))
            {
                if (under is not null) return;
                supersessionRefusals++;
                // Guarded, because the arguments are evaluated whether or not Debug is enabled: this
                // runs once per refused fact, and on an ingestion where NOTHING qualifies that is once
                // per fact in the batch. Joining the relation list each time would be a real cost paid
                // for output nobody is reading. The batch warning below is unguarded on purpose --
                // it fires at most once and only when something is wrong.
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "Supersession skipped for '{Subject} {Predicate} {Object}': predicate is not "
                        + "declared single-valued, so at most one live value per subject is not "
                        + "assumed. Single-valued relations: {Known}.",
                        winner.Subject, winner.Predicate, winner.Object,
                        string.Join(", ", MemoryRelationCardinality.SingleValuedPredicates));
                }

                return;
            }

            // 36.4. A value that has ended, or not begun, replaces nothing: "I worked at Google until 2019" said after "I
            // work at Meta" is history, not a change of employer; "moving to Oslo next month" is a plan.
            if (!ReplacementShapes.HoldsNow(winner.ValidFrom ?? winner.OccurredOn, winner.ValidUntil, now))
                return;

            if (under is null) supersessionEligible++;

            var scope = string.IsNullOrEmpty(ownerId) ? null : MemoryScope.For(ownerId, includeShared: false);
            // G-15 review: a write without an owner replaces only owner-less (shared) facts. Read with no scope,
            // a shared write superseded every tenant's private facts with the same subject and predicate. The
            // supersede statement itself already refuses to link facts of different owners.
            var candidates = SharedScopes.OwnedOrShared(ownerId);
            try
            {
                // J-6: one clock. The winner was judged to hold at `now`; the candidates are judged at the same instant.
                var losers = await _factRepository.FindSupersededCandidatesAsync(
                    winner.FactId, said.Subject, said.Predicate, winner.Object, now, candidates,
                    cancellationToken).ConfigureAwait(false);
                // I-5. A fact now stored under the user's name also replaces what was stored before the
                // name was known, under the words used then ("user | lives in | Lisbon").
                if (said.Metadata.TryGetValue("subject_surface", out var surface) && surface is string surfaceSubject &&
                    MemoryUserNames.IsSelf(surfaceSubject))
                {
                    losers = [.. losers, .. await _factRepository.FindSupersededCandidatesAsync(
                        winner.FactId, surfaceSubject, said.Predicate, winner.Object, now, candidates,
                        cancellationToken).ConfigureAwait(false)];
                }

                foreach (var loser in losers.DistinctBy(fact => fact.FactId).Where(loser => loser.FactId != winner.FactId))
                {
                    await _factClosures.CloseAsync(
                        loser.FactId, winner.FactId, ClosingOf(loser, winner), ChangedAt(winner, now), scope, cancellationToken)
                        .ConfigureAwait(false);
                    _logger.LogDebug(
                        "Superseded fact '{Loser}' with '{Winner}' ({S} {P}).",
                        loser.FactId, winner.FactId, winner.Subject, winner.Predicate);
                    // 38.6. Its edge too ("Lena —lives in→ Lyon" stayed live beside "moved to → Copenhagen"). The relation
                    // is single-valued, so the winner is the only value still said.
                    withdrawn.Add(new WithdrawnFact(loser, winner, [winner], candidates, scope, Correction: false));
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

        // 36.4. A correction the extractor marked closes the live fact that stated the replaced value, when
        // nothing else would (a plan, a value under a multi-valued relation). Same gate and same failure policy
        // as supersession above: best-effort, never the reason a stored memory is reported as failed.
        async Task CloseCorrectedAsync(Fact winner, string replaced, string sourceKey)
        {
            if (!SupersedeReplacedFacts) return;
            // No start guard, unlike supersession: a marked correction is the person withdrawing what they said,
            // now ("the full marathon in May instead of the half in April" retracts the half today, though the full
            // marathon is dated May; found in simulated conversations after a guard here left both plans live).
            // Read like supersession reads (own, or shared only for a shared write); write with the owner's scope, or
            // none for a shared write: the supersede statement already refuses to link facts of different owners.
            var readScope = SharedScopes.OwnedOrShared(ownerId);
            var writeScope = string.IsNullOrEmpty(ownerId) ? null : MemoryScope.For(ownerId, includeShared: false);
            try
            {
                var candidates = (await _factRepository.GetBySubjectAsync(winner.Subject, readScope, cancellationToken)
                    .ConfigureAwait(false)).ToList();
                // As supersession does: the subject as it was stored before the user's name was known.
                if (winner.Metadata.TryGetValue("subject_surface", out var surface) && surface is string said &&
                    !string.Equals(said, winner.Subject, StringComparison.Ordinal))
                    candidates.AddRange(await _factRepository.GetBySubjectAsync(said, readScope, cancellationToken)
                        .ConfigureAwait(false));
                // No order guard: a marked correction names what it replaces, wherever that was said, except a current
                // value of this extraction. Its fallback (another relation) spares what this extraction created and what
                // it said after the correction: that is what the person holds now.
                var saidAt = factIndex.GetValueOrDefault(sourceKey);
                var spared = new HashSet<string>(createdHere, StringComparer.Ordinal);
                foreach (var (key, fact) in factsByKey)
                {
                    if (factIndex.GetValueOrDefault(key) > saidAt) spared.Add(fact.FactId);
                }
                // B-13 (the branch review). With BitemporalChanges a change keeps the old value believed as history, so a
                // correction of it ("I never lived in Bilbao") must reach it too. Only when the correction is new: a value said
                // again ("I moved to Madrid, not Bilbao", Madrid already stored) is a restatement, and must not erase the past.
                var closed = Corrections.Closed(
                    candidates.Where(candidate => !currentFactIds.Contains(candidate.FactId)), winner, replaced, spared,
                    includeHistory: _options.BitemporalChanges && createdHere.Contains(winner.FactId));
                foreach (var loser in closed)
                {
                    await _factClosures.CloseAsync(loser.FactId, winner.FactId, FactClosureReason.Correction, null, writeScope, cancellationToken)
                        .ConfigureAwait(false);
                    _logger.LogDebug("Correction '{Winner}' closed fact '{Loser}' (replaces '{Replaced}').",
                        winner.FactId, loser.FactId, replaced);
                }
                if (closed.Count > 0)
                {
                    var closedIds = closed.Select(fact => fact.FactId).ToHashSet(StringComparer.Ordinal);
                    var stillLive = candidates
                        .Where(fact => fact.InvalidatedAtUtc is null && !closedIds.Contains(fact.FactId))
                        .Append(winner)
                        .ToList();
                    foreach (var loser in closed)
                        withdrawn.Add(new WithdrawnFact(loser, winner, stillLive, readScope, writeScope, Correction: true));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Closing what fact '{Id}' corrects failed; it remains stored alongside it.", winner.FactId);
            }
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
        var distinctExtractedTriples = prepared.Facts
            .Select(fact => (
                MemoryTripleCanonicalizer.CanonicalValue(factNames.StoredName(fact.Item, subject: true)),
                MemoryTripleCanonicalizer.Canonical(fact.Item.Predicate),
                MemoryTripleCanonicalizer.CanonicalValue(factNames.StoredName(fact.Item, subject: false))))
            .Distinct()
            .Count() == prepared.Facts.Count;
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
                // Every fact of the batch is already stored: what it created, and which fact is which, is known before
                // any correction runs.
                foreach (var input in factInputs)
                {
                    var written = batchedFactsByKey[FactKey(input.Item)];
                    if (written.FactId == input.Item.FactId) createdHere.Add(input.Item.FactId);
                    Written(input.SourceKey, extractedByKey.GetValueOrDefault(input.SourceKey), written);
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
            foreach (var preparedFact in prepared.Facts)
            {
                if (await PrepareFactAsync(preparedFact).ConfigureAwait(false) is { } input)
                    await PersistFactIndividuallyAsync(input.Item, input.SourceKey).ConfigureAwait(false);
            }
        }
        if (SupersedeReplacedFacts)
            await ResolveWrittenFactsAsync().ConfigureAwait(false);

        // 36.4. Everything this extraction wrote is in the store: now, and only now, it closes what it replaces. Marked
        // corrections first (they name what they replace, and supersession would otherwise close that and leave the
        // fallback to find something else); then each current value's supersession; then the values it replaced.
        async Task ResolveWrittenFactsAsync()
        {
            var once = writtenFacts.DistinctBy(written => written.Key, StringComparer.Ordinal).ToList();
            foreach (var (key, fact) in once)
            {
                if (factCorrections.TryGetValue(key, out var replaced))
                    await CloseCorrectedAsync(fact, replaced, key).ConfigureAwait(false);
            }
            foreach (var (key, fact) in once)
            {
                if (!replacedFacts.ContainsKey(key))
                    await SupersedeReplacedFactsAsync(fact).ConfigureAwait(false);
            }
            await CloseReplacedFactsAsync(once.Where(written => replacedFacts.ContainsKey(written.Key)).ToList()).ConfigureAwait(false);
        }

        // 36.4. The values this extraction replaced, closed by the one it left current now that everything is written,
        // with what each would have superseded: it may be stored under another self word or another form of the relation
        // than the current value's ("user lives in Lisbon" before "I live in Oslo"). Read first: supersession is
        // idempotent on the edge but lowers confidence again. A current value that failed to write is stood in for by the
        // last replaced value the person did not correct away, latest first (the same order the current value is chosen
        // by); none, and nothing is closed. Best-effort, as every closing is.
        async Task CloseReplacedFactsAsync(List<(string Key, Fact Fact)> replacedWritten)
        {
            var writeScope = string.IsNullOrEmpty(ownerId) ? null : MemoryScope.For(ownerId, includeShared: false);
            foreach (var group in replacedWritten.GroupBy(r => replacedFacts[r.Key], StringComparer.Ordinal))
            {
                var members = group.ToList();
                if (!factsByKey.TryGetValue(group.Key, out var current))
                {
                    var standIn = members
                        .Where(member => !factDecision.Old.Contains(member.Key))
                        .OrderBy(member => extractedByKey.TryGetValue(member.Key, out var said)
                            ? said.ValidFrom ?? said.OccurredOn ?? now : now)
                        .ThenBy(member => factIndex.GetValueOrDefault(member.Key))
                        .Select(member => member.Fact)
                        .LastOrDefault();
                    if (standIn is null)
                    {
                        // Nothing left to stand in (the correction failed to write): a value this extraction created
                        // and itself corrected away is withdrawn, not left live beside the stored one.
                        foreach (var (key, fact) in members.Where(member => factDecision.Old.Contains(member.Key) &&
                                                                           createdHere.Contains(member.Fact.FactId)))
                        {
                            try
                            {
                                await _factRepository.InvalidateAsync(fact.FactId, writeScope, cancellationToken).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Withdrawing '{Key}' failed; it stays stored.", key);
                            }
                        }
                        continue;
                    }
                    current = standIn;
                    currentFactIds.Add(current.FactId);
                    await SupersedeReplacedFactsAsync(current).ConfigureAwait(false);
                }
                foreach (var (memberKey, member) in members.Where(member => member.Fact.FactId != current.FactId))
                {
                    try
                    {
                        if (await _factRepository.GetByIdAsync(member.FactId, cancellationToken).ConfigureAwait(false) is
                            { InvalidatedAtUtc: null })
                        {
                            await _factClosures.CloseAsync(member.FactId, current.FactId,
                                    factDecision.Old.Contains(memberKey) ? FactClosureReason.Correction : FactClosureReason.Change,
                                    ChangedAt(current, now), writeScope, cancellationToken)
                                .ConfigureAwait(false);
                            _logger.LogDebug("'{Winner}' closed '{Loser}', replaced in the same extraction.", current.FactId, member.FactId);
                        }
                        // What the replaced value would have superseded, the current one supersedes, with every guard.
                        await SupersedeReplacedFactsAsync(current, under: member).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Closing replaced value '{Id}' failed; it remains stored alongside the current one.", member.FactId);
                    }
                }
            }
        }

        var persistedPreferences = await _preferencePersistence.PersistAsync(
            prepared.Preferences, ownerId, trustLevel, sourceMessageIds, outcomes, failFast,
            extraction.WrittenByWriter, cancellationToken).ConfigureAwait(false);
        var preferencesByKey = persistedPreferences.BySourceKey;
        var persistedRelationships = await _relationshipPersistence.PersistAsync(
            extraction.FilteredRelationships, persistedEntityMap, userName, ownerId, sourceMessageIds,
            outcomes, failFast, extraction.WrittenByWriter, cancellationToken).ConfigureAwait(false);
        await _edgeEnding.ApplyAsync(withdrawn, cancellationToken).ConfigureAwait(false);

        // The batch-level signal. Debug-level per-fact logging tells you WHY once you are already
        // looking; this is what makes you look. Warned once per batch rather than per fact so a large
        // ingestion cannot bury it, and only when the option was actually asked for -- a warning on a
        // feature nobody enabled is how warnings stop being read.
        if (SupersedeReplacedFacts && supersessionEligible == 0 && supersessionRefusals > 0)
        {
            _logger.LogWarning(
                "SupersedeReplacedFacts is ENABLED but none of the {Refused} fact(s) in this batch "
                + "had a predicate declared single-valued, so NO supersession was attempted and no "
                + ":SUPERSEDED_BY edge was written. The feature is on and inert for this content. "
                + "Single-valued relations: {Known}. Either the extracted predicates need to "
                + "canonicalise into that set, or supersession does not apply to this material.",
                supersessionRefusals,
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
                [.. factsByKey.Values.Where(fact => createdHere.Contains(fact.FactId)).DistinctBy(fact => fact.FactId)],
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
            FactCount = persistedFactCount,
            PreferenceCount = persistedPreferences.Count,
            RelationshipCount = persistedRelationships.Count,
            Outcomes = outcomes
        };
    }

    /// <summary>
    /// I-5: the name the user gave. This extraction's naming fact wins (the latest one in it); otherwise
    /// the owner's latest live stored one; otherwise none, and nothing is rewritten.
    /// </summary>
    private async Task<string?> UserNameAsync(
        IReadOnlyList<PreparedFact> facts, string ownerId, CancellationToken cancellationToken)
    {
        var stated = facts.Select(f => f.Item).LastOrDefault(MemoryUserNames.IsNamingFact)?.Object;
        if (!string.IsNullOrWhiteSpace(stated)) return stated.Trim();

        // Best-effort: a failed lookup leaves the facts under the words used, it never fails the persist.
        try
        {
            var stored = await _factRepository.FindLatestObjectAsync(
                MemoryUserNames.SelfWords, MemoryUserNames.NamingPredicates, MemoryScope.For(ownerId, includeShared: false),
                cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(stored) ? null : stored.Trim();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the user's name for owner {Owner}; facts keep the words used.", ownerId);
            return null;
        }
    }
}
