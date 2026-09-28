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
        var started = _clock.UtcNow;
        var result = await PersistCoreAsync(extraction, ownerId, trustLevel, cancellationToken)
            .ConfigureAwait(false);

        // J-11. After the write (whichever path produced it), outside its transaction: a name that replaced another
        // renames what it named. Before the rebuild below, so the working-memory block is compiled from the result.
        await RenameCorrectedNamesAsync(extraction, ownerId, started, cancellationToken).ConfigureAwait(false);

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
        if (_options.SupersedeReplacedFacts && extraction.FilteredFacts.Count > 0)
            extraction = extraction with
            {
                FilteredFacts = ReplacementShapes.Prepare(
                    extraction.FilteredFacts,
                    name => extraction.ResolvedEntityMap.TryGetValue(name, out var entity) ? entity.Type : null,
                    _clock.UtcNow),
            };

        var prepared = await PrepareEmbeddingsAsync(extraction, cancellationToken).ConfigureAwait(false);
        if (_options.DeduplicateWithinExtraction && prepared.Facts.Count > 1)
        {
            var (kept, merged) = WithoutNearDuplicates(prepared.Facts, _options.WithinExtractionDuplicateThreshold);
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

    /// <summary>
    /// J-11 (<see cref="ExtractionOptions.RenameOnCorrectedName"/>). The renames this persist made: for each naming fact
    /// it wrote ("user | is named | Priya"), the naming facts of the same subject it closed ("… | Pruya"), found in the
    /// store rather than threaded through the write, so it holds for every write path (atomic, best-effort, replayed).
    /// Best-effort, like supersession: a failed rename leaves the two names side by side, never a failed ingestion.
    /// </summary>
    private async Task RenameCorrectedNamesAsync(
        ExtractionStageResult extraction, string? ownerId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        if (!_options.RenameOnCorrectedName || string.IsNullOrWhiteSpace(ownerId)) return;
        var naming = extraction.FilteredFacts
            .Where(f => UserNames.IsNamingPredicate(f.Predicate) && !string.IsNullOrWhiteSpace(f.Object))
            .ToList();
        if (naming.Count == 0) return;

        var scope = MemoryScope.For(ownerId, includeShared: false);
        // The store's clock stamps invalidated_at; a second of slack keeps a close made by this persist inside.
        var from = since - TimeSpan.FromSeconds(1);
        foreach (var named in naming)
        {
            var name = named.Object.Trim();
            try
            {
                var replaced = (await _factRepository.GetBySubjectAsync(named.Subject, scope, cancellationToken).ConfigureAwait(false))
                    .Where(f => UserNames.IsNamingPredicate(f.Predicate) && f.InvalidatedAtUtc >= from &&
                                !string.Equals(f.Object.Trim(), name, StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.Object.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                foreach (var old in replaced)
                    await RenameAsync(old, name, scope, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Renaming to '{Name}' failed; the old name stays beside it.", name);
            }
        }
    }

    /// <summary>J-11: the entity called <paramref name="old"/> becomes <paramref name="name"/>, and so do the facts about it.</summary>
    private async Task RenameAsync(string old, string name, MemoryScope scope, CancellationToken cancellationToken)
    {
        // The entity: only one that bears the old name as its own (an alias match is already the renamed one).
        var source = await _entityRepository.FindLiveByNameAsync(old, null, scope, cancellationToken).ConfigureAwait(false);
        if (source is not null && string.Equals(source.Name, old, StringComparison.OrdinalIgnoreCase))
        {
            var target = await _entityRepository.FindLiveByNameAsync(name, null, scope, cancellationToken).ConfigureAwait(false)
                ?? await _entityRepository.UpsertAsync(new Entity
                {
                    EntityId = _idGenerator.GenerateId(),
                    Name = name,
                    Type = source.Type,
                    Subtype = source.Subtype,
                    Confidence = source.Confidence,
                    OwnerId = scope.OwnerId,
                    CreatedAtUtc = _clock.UtcNow,
                    SourceMessageIds = source.SourceMessageIds,
                    Embedding = await _embeddingOrchestrator.EmbedAsync(name, cancellationToken).ConfigureAwait(false),
                }, cancellationToken).ConfigureAwait(false);
            if (target.EntityId == source.EntityId)
            {
                // Entity resolution already filed the new name under the old entity (as an alias): it is the same one,
                // so it is renamed in place, the new name its own and the old one kept as an alias.
                await _entityRepository.UpsertAsync(source with
                {
                    Name = name,
                    CanonicalName = name,
                    Aliases = [.. source.Aliases.Where(a => !string.Equals(a, name, StringComparison.OrdinalIgnoreCase))
                                   .Append(old).Distinct(StringComparer.OrdinalIgnoreCase)],
                    Embedding = await _embeddingOrchestrator.EmbedAsync(name, cancellationToken).ConfigureAwait(false),
                }, cancellationToken).ConfigureAwait(false);
                _logger.LogDebug("Renamed entity '{Old}' to '{Name}' in place ({Id}).", old, name, source.EntityId);
            }
            else
            {
                // Relationships and mentions move, the old name becomes an alias; then the old entity is closed.
                await _entityRepository.MergeEntitiesAsync(source.EntityId, target.EntityId, scope, cancellationToken).ConfigureAwait(false);
                await _entityRepository.InvalidateAsync(source.EntityId, scope, cancellationToken).ConfigureAwait(false);
                _logger.LogDebug("Renamed entity '{Old}' to '{Name}' ({Source} into {Target}).", old, name, source.EntityId, target.EntityId);
            }
        }

        // The facts said about the old name, restated under the new one; each original is superseded by its restatement.
        var facts = await _factRepository.GetBySubjectAsync(old, scope, cancellationToken).ConfigureAwait(false);
        foreach (var fact in facts.Where(f => f.InvalidatedAtUtc is null))
        {
            var metadata = new Dictionary<string, object>(fact.Metadata) { ["renamed_from"] = old };
            var restated = await _factRepository.UpsertAsync(fact with
            {
                FactId = _idGenerator.GenerateId(),
                Subject = name,
                CreatedAtUtc = _clock.UtcNow,
                Metadata = metadata,
                Embedding = await _embeddingOrchestrator.EmbedFactAsync(name, fact.Predicate, fact.Object, cancellationToken)
                    .ConfigureAwait(false),
            }, cancellationToken).ConfigureAwait(false);
            if (restated.FactId != fact.FactId)
                await _factRepository.SupersedeAsync(fact.FactId, restated.FactId, scope, cancellationToken).ConfigureAwait(false);
        }
    }

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

        // COUNTED SEPARATELY FROM THE MAP, because E-1 made the map's keys and its entities two
        // different quantities: it is keyed by name AND by every captured alias, so one entity with
        // two names occupies two slots. Reporting the map's Count as the entity count would inflate
        // ingestion telemetry by exactly the number of aliases captured -- and the store probes the
        // alias measurement itself reads are built on these counts, so the feature would have
        // corrupted the census meant to judge it.
        // I-5. "user" is the owner: once the user has said their name, what they say about themselves is
        // stored under it. Read only when this extraction has something to rewrite.
        var userName = _options.ResolveUserToName && !string.IsNullOrWhiteSpace(ownerId) &&
                       (prepared.Facts.Any(f => UserNames.MeansUser(f.Item, subject: true) || UserNames.MeansUser(f.Item, subject: false)) ||
                        prepared.Entities.Keys.Any(UserNames.IsSelf) ||
                        extraction.FilteredRelationships.Any(r => UserNames.MeansUserEndpoint(r.SourceEntity, source: true) ||
                                                                  UserNames.MeansUserEndpoint(r.TargetEntity, source: false)))
            ? await UserNameAsync(prepared.Facts, ownerId!, cancellationToken).ConfigureAwait(false)
            : null;

        var persistedEntityIds = new HashSet<string>(StringComparer.Ordinal);
        // 36.6 (D-6). The speaker is not an entity called "user": the prompt calls the speaker "the user", and the
        // model lists "user" among the people (found live: a "user" node beside the person's own, with relationships
        // hanging from it). Once the user's name is known it is not stored, and a relationship from a self word lands
        // on the person's own entity (I-7). Until then it is stored as before, so no relationship is lost.
        // With the name known, the speaker's entity IS the named person: written under that name (so a relationship
        // from "user" lands on it), or, when that person is already an entity here or in the store, not written at all
        // (I-7 then finds that one). Never a node called "user".
        var speakerIsKnown = false;
        if (userName is not null && prepared.Entities.Keys.Any(UserNames.IsSelf))
        {
            speakerIsKnown = prepared.Entities.Keys.Any(key => string.Equals(key, userName, StringComparison.OrdinalIgnoreCase));
            if (!speakerIsKnown)
            {
                try
                {
                    speakerIsKnown = await _entityRepository.FindLiveByNameAsync(
                        userName, "PERSON", MemoryScope.For(ownerId!, includeShared: false), cancellationToken).ConfigureAwait(false) is not null;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not read the user's entity for owner {Owner}; the speaker is written under their name.", ownerId);
                }
            }
        }
        // The speaker once: "user" and "I" in one extraction are one person, and a second self word maps to the first.
        var speakerKey = userName is null ? null : prepared.Entities.Keys.FirstOrDefault(UserNames.IsSelf);
        float[]? speakerVector = null;
        if (speakerKey is not null && !speakerIsKnown)
        {
            try
            {
                // Embedded as the name it is written under: the vector of "user" would make every owner's speaker alike.
                speakerVector = await _embeddingOrchestrator.EmbedAsync(userName!, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Best-effort writes the entity without a vector, so no failure is recorded against a written item.
                if (failFast)
                    RecordFailureAndMaybeThrow(outcomes, failFast, MemoryItemKind.Entity, IngestionStage.Embedding,
                        MemoryErrorCodes.EmbeddingGenerationFailed, speakerKey, null, ex,
                        "Ingestion failed fast: embedding generation failed for the user's name.");
                _logger.LogWarning(ex, "Could not embed the user's name; their entity is written without a vector.");
            }
        }
        var entityInputs = prepared.Entities
            .Where(pair => userName is null || !UserNames.IsSelf(pair.Key) || (!speakerIsKnown && pair.Key == speakerKey))
            .Select(pair => userName is not null && UserNames.IsSelf(pair.Key)
                ? new KeyValuePair<string, Entity>(pair.Key, pair.Value with
                {
                    Name = userName, CanonicalName = userName, Type = "PERSON",
                    Embedding = speakerVector is { Length: > 0 } ? speakerVector : null,
                })
                : pair)
            .Select(pair =>
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
            // The speaker, written under the user's name, is found by that name (and so from every self word, which
            // resolves to the user's entity): the name is the speaker's, never an alias recorded by another entity.
            if (name == speakerKey && userName is not null)
                persistedEntityMap[userName] = persisted;
            persistedEntityIds.Add(persisted.EntityId);

            // E-1. THE MAP IS WHAT DECIDES WHETHER A FACT FINDS ITS ENTITY, and it was keyed by the
            // extracted NAME alone. So a store could hold an entity that knows "head office" is also
            // "the Calderwick office" and still never link the facts phrased the second way --
            // exactly the one-directional loss the alias census measured, where 14 of 15 answers
            // undercounted and none ever over-counted.
            //
            // Unconditional on purpose: with no aliases captured this loop does nothing, so it
            // cannot move any measurement taken before aliases existed. Whether aliases are captured
            // at all is LlmExtractionOptions.CaptureIdentityAliases, one layer up.
            //
            // A NAME NEVER LOSES TO AN ALIAS. If two entities claim one string -- one as its name,
            // one as an alias -- the name is the stronger claim and keeps the slot. TryAdd, not
            // assignment, so the first alias to claim a free slot keeps it and ordering cannot
            // silently decide which entity a fact attaches to.
            foreach (var alias in persisted.Aliases)
            {
                if (!string.IsNullOrWhiteSpace(alias))
                    persistedEntityMap.TryAdd(alias, persisted);
            }

            // I-2. A canonical fact names the entity by its resolved name, which may be none of the
            // strings this extraction produced ("Tomás" resolved to "Tomás Silva"): the name must find
            // the entity too, or the fact would lose its ABOUT edge. Gated so the default map is unchanged.
            if (_options.CanonicalFactSubjects && !string.IsNullOrWhiteSpace(persisted.Name))
                persistedEntityMap.TryAdd(persisted.Name, persisted);

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
        // 36.4. What each marked correction replaces, by the words it was extracted as (the source key every
        // outcome of this persist is keyed by). Empty unless the extractor was asked to mark corrections. A correction
        // naming its own value marks nothing: the extractor repeated the value, and the fact is an ordinary statement.
        var factCorrections = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var preparedFact in prepared.Facts)
        {
            var item = preparedFact.Item;
            if (string.IsNullOrWhiteSpace(item.Replaces)) continue;
            var value = Corrections.Value(CanonicalName(item.Replaces!));
            if (value.Length == 0 || value == Corrections.Value(StoredName(item, subject: false))) continue;
            factCorrections.TryAdd(FactSourceKey(item), item.Replaces!);
        }
        // Which value this extraction leaves current (CurrentValues), in stored terms: the subject as stored (the self
        // words one speaker), the value as stored. A replaced fact supersedes nothing; its current value closes it once
        // every fact is written, on either write path.
        string FactSubjectKey(ExtractedFact fact)
        {
            var stored = StoredName(fact, subject: true);
            return UserNames.IsSelf(stored) ? "\u0001self" : MemoryTripleCanonicalizer.CanonicalValue(stored);
        }
        // A value that has ended, or not begun, states no current value ("lived in Berlin until 2019", "moving to Oslo
        // next month") and joins no group: one rule (ReplacementShapes.HoldsNow) with the supersession query's.
        var now = _clock.UtcNow;
        string? FactRelationKey(ExtractedFact fact) =>
            ReplacementShapes.HoldsNow(fact.ValidFrom ?? fact.OccurredOn, fact.ValidUntil, now) &&
            MemoryRelationCardinality.Relation(fact.Predicate) is { } relation
                ? FactSubjectKey(fact) + "\u0001" + relation
                : null;
        var factDecision = !_options.SupersedeReplacedFacts
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
                    Corrections.Value(StoredName(fact, subject: false)) == Corrections.Value(CanonicalName(named)),
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
        var supersessionRefusals = 0;

        // I-2. The name a subject or object is stored under: the resolved entity's, when it resolved to one.
        string CanonicalName(string surface) =>
            _options.CanonicalFactSubjects && !string.IsNullOrWhiteSpace(surface) &&
            persistedEntityMap.TryGetValue(surface, out var resolved) && !string.IsNullOrWhiteSpace(resolved.Name)
                ? resolved.Name
                : surface;



        // The one place a stored subject or object is decided: the fact's preparation and the batch
        // distinctness guard both call it, so they cannot disagree about which facts become one node.
        string StoredName(ExtractedFact fact, bool subject)
        {
            var surface = subject ? fact.Subject : fact.Object;
            if (userName is not null && UserNames.MeansUser(fact, subject)) return CanonicalName(userName);
            // The words that mean the user are never renamed to an entity's name: found live, "user" resolved into
            // the person "Dana" and canonical subjects stored the naming fact as "Dana | is named | Dana", so the
            // name could never be found again (it is looked up under "user"). Only the rule above renames them.
            if (subject && UserNames.IsSelf(surface)) return surface;
            return CanonicalName(surface);
        }

        // The source key stays the words as extracted: outcomes are keyed by the input item.
        static string FactSourceKey(ExtractedFact fact) => $"{fact.Subject} {fact.Predicate} {fact.Object}";

        async Task<(Fact Item, string SourceKey)?> PrepareFactAsync(PreparedFact preparedFact)
        {
            var extracted = preparedFact.Item;
            var factSourceKey = FactSourceKey(extracted);
            var subject = StoredName(extracted, subject: true);
            var @object = StoredName(extracted, subject: false);
            try
            {
                // Trust is monotonic for owner-scoped facts. The pre-fetch deliberately excludes shared
                // facts and carries an existing triple's casing forward so an exact MERGE cannot create a
                // casing-only duplicate. Rank 20 will make this read-modify-write atomic; feat-04 leaves
                // that owner boundary and trust behavior unchanged.
                Fact? existingFact = string.IsNullOrEmpty(ownerId)
                    ? null
                    : await _factRepository.FindByTripleAsync(
                        subject, extracted.Predicate, @object,
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
                if (!string.Equals(subject, extracted.Subject, StringComparison.Ordinal) ||
                    !string.Equals(@object, extracted.Object, StringComparison.Ordinal))
                {
                    var surfaces = new Dictionary<string, object>(factMetadata);
                    if (!string.Equals(subject, extracted.Subject, StringComparison.Ordinal)) surfaces["subject_surface"] = extracted.Subject;
                    if (!string.Equals(@object, extracted.Object, StringComparison.Ordinal)) surfaces["object_surface"] = extracted.Object;
                    factMetadata = surfaces;
                }

                return (new Fact
                {
                    FactId = _idGenerator.GenerateId(),
                    Subject = existingFact?.Subject ?? subject,
                    Predicate = existingFact?.Predicate ?? extracted.Predicate,
                    Object = existingFact?.Object ?? @object,
                    Confidence = extracted.Confidence,
                    ValidFrom = extracted.ValidFrom,
                    ValidFromPrecision = extracted.ValidFromPrecision,
                    ValidUntil = extracted.ValidUntil,
                    ValidUntilPrecision = extracted.ValidUntilPrecision,
                    OccurredOn = extracted.OccurredOn,
                    OccurredOnPrecision = extracted.OccurredOnPrecision,
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
            RecordSuccess(outcomes, MemoryItemKind.Fact, sourceKey, persisted.FactId);
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
            if (!_options.SupersedeReplacedFacts) return;
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
                    UserNames.IsSelf(surfaceSubject))
                {
                    losers = [.. losers, .. await _factRepository.FindSupersededCandidatesAsync(
                        winner.FactId, surfaceSubject, said.Predicate, winner.Object, now, candidates,
                        cancellationToken).ConfigureAwait(false)];
                }

                foreach (var loser in losers.DistinctBy(fact => fact.FactId).Where(loser => loser.FactId != winner.FactId))
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

        // 36.4. A correction the extractor marked closes the live fact that stated the replaced value, when
        // nothing else would (a plan, a value under a multi-valued relation). Same gate and same failure policy
        // as supersession above: best-effort, never the reason a stored memory is reported as failed.
        async Task CloseCorrectedAsync(Fact winner, string replaced, string sourceKey)
        {
            if (!_options.SupersedeReplacedFacts) return;
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
                foreach (var loser in Corrections.Closed(
                             candidates.Where(candidate => !currentFactIds.Contains(candidate.FactId)), winner, replaced, spared))
                {
                    await _factRepository.SupersedeAsync(loser.FactId, winner.FactId, writeScope, cancellationToken)
                        .ConfigureAwait(false);
                    _logger.LogDebug("Correction '{Winner}' closed fact '{Loser}' (replaces '{Replaced}').",
                        winner.FactId, loser.FactId, replaced);
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
                MemoryTripleCanonicalizer.CanonicalValue(StoredName(fact.Item, subject: true)),
                MemoryTripleCanonicalizer.Canonical(fact.Item.Predicate),
                MemoryTripleCanonicalizer.CanonicalValue(StoredName(fact.Item, subject: false))))
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
        if (_options.SupersedeReplacedFacts)
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
                foreach (var member in members.Select(member => member.Fact).Where(member => member.FactId != current.FactId))
                {
                    try
                    {
                        if (await _factRepository.GetByIdAsync(member.FactId, cancellationToken).ConfigureAwait(false) is
                            { InvalidatedAtUtc: null })
                        {
                            await _factRepository.SupersedeAsync(member.FactId, current.FactId, writeScope, cancellationToken)
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

        // 3. Embed + upsert preferences.
        // 36.4. Preferences have no single-valued relation, so a marked correction is the only way one replaces
        // another ("Arcade Fire, not Radiohead" left both live).
        // A correction naming its own value ("Favourite band is Radiohead", replaces "Radiohead") marks nothing.
        var preferenceCorrections = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var preparedPreference in prepared.Preferences)
        {
            var item = preparedPreference.Item;
            if (string.IsNullOrWhiteSpace(item.Replaces) || Corrections.Value(item.Replaces) is not { Length: > 0 } named ||
                Corrections.StatedValue(item.PreferenceText) == named)
                continue;
            preferenceCorrections.TryAdd(item.PreferenceText, item.Replaces!);
        }
        // As for facts (CurrentValues): a relation is the category and the single-valued relation the text states
        // ("Favourite band is ..."); a correction names what of its category contains the value it replaces.
        var preferenceDecision = !_options.SupersedeReplacedFacts
            ? new CurrentValues.Decision(new Dictionary<string, string>(), new HashSet<string>())
            : CurrentValues.Decide(
                prepared.Preferences.Select(p => p.Item).ToList(),
                preference => preference.PreferenceText,
                preference => Corrections.SingleValuedRelation(preference.PreferenceText) is { } relation
                    ? MemoryTripleCanonicalizer.CanonicalValue(preference.Category) + "\u0001" + relation
                    : null,
                (preference, correction) =>
                    preferenceCorrections.TryGetValue(correction.PreferenceText, out var named) &&
                    string.Equals(preference.Category, correction.Category, StringComparison.OrdinalIgnoreCase) &&
                    Corrections.Names(preference.PreferenceText, named));
        var replacedPreferences = preferenceDecision.Replaced;
        var preferenceIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (preparedPreference, index) in prepared.Preferences.Select((p, i) => (p, i)))
            preferenceIndex.TryAdd(preparedPreference.Item.PreferenceText, index);
        var writtenPreferences = new List<(string Key, Preference Preference)>();
        var preferencesByKey = new Dictionary<string, Preference>(StringComparer.Ordinal);
        var currentPreferenceIds = new HashSet<string>(StringComparer.Ordinal);

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
            PreferenceWritten(sourceKey, persisted);

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

            writtenPreferences.Add((sourceKey, persisted));

            persistedPrefCount++;
            _logger.LogDebug("Persisted preference in category '{Category}'.", persisted.Category);
        }

        // A marked correction closes what it names; a current preference stating a single-valued relation ("Favourite
        // band is Arcade Fire") closes the other values of it, marked or not.
        async Task CloseCorrectedPreferencesAsync(Preference winner, string? replaced, bool sameRelation)
        {
            if (!_options.SupersedeReplacedFacts) return;
            var readScope = SharedScopes.OwnedOrShared(ownerId);
            var writeScope = string.IsNullOrEmpty(ownerId) ? null : MemoryScope.For(ownerId, includeShared: false);
            try
            {
                var candidates = (await _preferenceRepository.GetByCategoryAsync(winner.Category, readScope, cancellationToken)
                    .ConfigureAwait(false)).Where(candidate => !currentPreferenceIds.Contains(candidate.PreferenceId));
                foreach (var loser in candidates.Where(candidate =>
                             (replaced is not null && Corrections.Closes(candidate, winner, replaced)) ||
                             (sameRelation && Corrections.Replaces(winner, candidate))))
                {
                    await _preferenceRepository.SupersedeAsync(loser.PreferenceId, winner.PreferenceId, writeScope, cancellationToken)
                        .ConfigureAwait(false);
                    _logger.LogDebug("Correction '{Winner}' closed preference '{Loser}' (replaces '{Replaced}').",
                        winner.PreferenceId, loser.PreferenceId, replaced);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Closing what preference '{Id}' corrects failed; it remains stored alongside it.", winner.PreferenceId);
            }
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

        void PreferenceWritten(string sourceKey, Preference persisted)
        {
            preferencesByKey.TryAdd(sourceKey, persisted);
            if (!replacedPreferences.ContainsKey(sourceKey) && Corrections.SingleValuedRelation(persisted.PreferenceText) is not null)
                currentPreferenceIds.Add(persisted.PreferenceId);
        }

        if (batchedPreferencesById is not null)
        {
            foreach (var input in preferenceInputs)
                PreferenceWritten(input.SourceKey, batchedPreferencesById[input.Item.PreferenceId]);
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
        if (_options.SupersedeReplacedFacts)
        {
            // As for facts, once every preference is written: what each one closes (only a current one replaces the
            // other values of its relation; a replaced one still closes what it names, older than both), then each
            // replaced one closed by its current one, a failed current stood in for by the latest one not corrected away.
            var once = writtenPreferences.DistinctBy(written => written.Key, StringComparer.Ordinal).ToList();
            foreach (var (key, persisted) in once)
            {
                var isCurrent = !replacedPreferences.ContainsKey(key);
                preferenceCorrections.TryGetValue(key, out var replaced);
                if (replaced is not null || (isCurrent && Corrections.SingleValuedRelation(persisted.PreferenceText) is not null))
                    await CloseCorrectedPreferencesAsync(persisted, replaced, sameRelation: isCurrent).ConfigureAwait(false);
            }
            var writeScope = string.IsNullOrEmpty(ownerId) ? null : MemoryScope.For(ownerId, includeShared: false);
            foreach (var group in once.Where(w => replacedPreferences.ContainsKey(w.Key)).GroupBy(w => replacedPreferences[w.Key], StringComparer.Ordinal))
            {
                if (!preferencesByKey.TryGetValue(group.Key, out var current))
                {
                    var standIn = group.Where(member => !preferenceDecision.Old.Contains(member.Key))
                        .OrderBy(member => preferenceIndex.GetValueOrDefault(member.Key))
                        .Select(member => member.Preference)
                        .LastOrDefault();
                    if (standIn is null) continue;
                    current = standIn;
                    currentPreferenceIds.Add(current.PreferenceId);
                    await CloseCorrectedPreferencesAsync(current, replaced: null, sameRelation: true).ConfigureAwait(false);
                }
                foreach (var member in group.Select(member => member.Preference).Where(member => member.PreferenceId != current.PreferenceId))
                {
                    try
                    {
                        if (await _preferenceRepository.GetByIdAsync(member.PreferenceId, cancellationToken).ConfigureAwait(false) is not
                            { InvalidatedAtUtc: null })
                            continue;
                        await _preferenceRepository.SupersedeAsync(member.PreferenceId, current.PreferenceId, writeScope, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Closing replaced preference '{Id}' failed; it remains stored alongside the current one.", member.PreferenceId);
                    }
                }
            }
        }

        // 4. Persist relationships — resolve entity IDs from the upserted entity map.
        // I-7. "user" as an endpoint is the user's person entity: the one this extraction wrote under their
        // name, else the stored one (read once, best-effort). Unknown name or entity: skipped as before.
        Entity? userEntity = null;
        var userEntityRead = false;
        async Task<Entity?> UserEntityAsync()
        {
            if (userEntityRead || userName is null) return userEntity;
            userEntityRead = true;
            if (persistedEntityMap.TryGetValue(userName, out var inThisExtraction)) return userEntity = inThisExtraction;
            try
            {
                // Live only: a name merged into another person, or invalidated, must not anchor new edges.
                return userEntity = await _entityRepository.FindLiveByNameAsync(
                    userName, "PERSON", MemoryScope.For(ownerId!, includeShared: false), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read the user's entity for owner {Owner}; relationships from \"user\" are skipped.", ownerId);
                return null;
            }
        }
        async Task<Entity?> EndpointAsync(string name, bool source) =>
            persistedEntityMap.TryGetValue(name, out var entity) ? entity
            : UserNames.MeansUserEndpoint(name, source) ? await UserEntityAsync().ConfigureAwait(false)
            : null;

        // 36.7 (D-8c). An extracted relationship is the live edge it restates, not a second one: its id is that
        // edge's id. The store merges relationships on their id, and extraction gave every one a fresh id, so the same
        // relation said twice ("Nadia lives in Lyon", then asked "when did I move to Lyon?") became two edges. Read
        // once per source entity and cached for this persist; a store that cannot read them writes fresh ids as before.
        var edgeScope = SharedScopes.OwnedOrShared(ownerId);
        var edgesBySource = new Dictionary<string, IReadOnlyList<Relationship>>(StringComparer.Ordinal);
        var idsByEdge = new Dictionary<(string Source, string Type, string Target), string>();
        async Task<IReadOnlyList<Relationship>> EdgesFromAsync(string sourceId)
        {
            if (edgesBySource.TryGetValue(sourceId, out var cached)) return cached;
            IReadOnlyList<Relationship> edges;
            try
            {
                edges = await _relationshipRepository.GetBySourceEntityAsync(sourceId, edgeScope, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read the relationships of entity '{Id}'; new ones get fresh ids.", sourceId);
                edges = [];
            }
            return edgesBySource[sourceId] = edges;
        }
        static string EdgeType(string type) => MemoryTripleCanonicalizer.Canonical(type);
        bool IsLive(Relationship edge) => edge.ValidUntil is not { } until || until > _clock.UtcNow;

        var relationshipInputs = new List<(Relationship Item, string SourceKey)>(
            extraction.FilteredRelationships.Count);
        foreach (var extracted in extraction.FilteredRelationships)
        {
            var relSourceKey = $"{extracted.SourceEntity}-{extracted.RelationshipType}->{extracted.TargetEntity}";

            if (await EndpointAsync(extracted.SourceEntity, source: true).ConfigureAwait(false) is not { } sourceEntity)
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

            if (await EndpointAsync(extracted.TargetEntity, source: false).ConfigureAwait(false) is not { } targetEntity)
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

            // I-7: "user -KNOWS-> Ana" said by Ana maps both ends to one person; no edge from a node to itself.
            if (string.Equals(sourceEntity.EntityId, targetEntity.EntityId, StringComparison.Ordinal))
            {
                outcomes.Add(new IngestionItemOutcome
                {
                    Kind = MemoryItemKind.Relationship,
                    Stage = IngestionStage.RelationshipPersistence,
                    Status = IngestionItemStatus.Skipped,
                    SourceKey = relSourceKey,
                    ErrorCode = MemoryErrorCodes.RelationshipEndpointNotPersisted,
                    ErrorMessage = "Both ends are the same entity.",
                });
                continue;
            }

            var edgeKey = (sourceEntity.EntityId, EdgeType(extracted.RelationshipType), targetEntity.EntityId);
            var restated = (await EdgesFromAsync(sourceEntity.EntityId).ConfigureAwait(false)).FirstOrDefault(edge =>
                edge.TargetEntityId == targetEntity.EntityId && EdgeType(edge.RelationshipType) == edgeKey.Item2 && IsLive(edge));
            if (!idsByEdge.TryGetValue(edgeKey, out var relationshipId))
                relationshipId = idsByEdge[edgeKey] = restated?.RelationshipId ?? _idGenerator.GenerateId();

            // A restated edge is the stored one, restated: the upsert matches it, so what is written is what was
            // stored, plus this statement's sources (the first statement's provenance kept, its validity, description
            // and attributes unchanged).
            relationshipInputs.Add((restated is not null
                ? restated with
                {
                    SourceMessageIds = restated.SourceMessageIds.Concat(sourceMessageIds).Distinct(StringComparer.Ordinal).ToList(),
                    Confidence = Math.Max(restated.Confidence, extracted.Confidence),
                    Description = restated.Description ?? extracted.Description,
                }
                : new Relationship
                {
                    RelationshipId = relationshipId,
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

        var persistedRelationshipIds = new HashSet<string>(StringComparer.Ordinal);
        void RecordPersistedRelationship(string sourceKey, Relationship persisted)
        {
            persistedRelationshipIds.Add(persisted.RelationshipId);
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

        // 36.4. A single-valued relation replaces its previous edge the way a fact replaces its previous value: a new
        // "lives_in Copenhagen" ends "lives_in Hamburg" (and "employed_by" a new firm ends "works_at" the old one),
        // non-destructively, by the edge's own valid_until. Found in simulated conversations: the facts were replaced
        // and both residence edges stayed live. Same gate as fact supersession; best-effort.
        if (_options.SupersedeReplacedFacts)
        {
            var endScope = string.IsNullOrEmpty(ownerId) ? null : MemoryScope.For(ownerId, includeShared: false);
            foreach (var (item, _) in relationshipInputs)
            {
                // Only after the replacement is stored: a failed write must not leave the person with no residence.
                if (!persistedRelationshipIds.Contains(item.RelationshipId)) continue;
                if (!MemoryRelationCardinality.IsSingleValued(item.RelationshipType)) continue;
                var replacedTypes = MemoryRelationCardinality.ReplacedKeys(item.RelationshipType);
                foreach (var previous in (await EdgesFromAsync(item.SourceEntityId).ConfigureAwait(false)).Where(edge =>
                             edge.TargetEntityId != item.TargetEntityId && IsLive(edge) &&
                             replacedTypes.Contains(EdgeType(edge.RelationshipType), StringComparer.Ordinal)))
                {
                    try
                    {
                        await _relationshipRepository.EndAsync(previous.RelationshipId, _clock.UtcNow, endScope, cancellationToken)
                            .ConfigureAwait(false);
                        _logger.LogDebug("Ended relationship '{Old}': replaced by '{New}'.", previous.RelationshipId, item.RelationshipId);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (NotSupportedException) { break; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Ending relationship '{Id}' failed; it stays beside its replacement.", previous.RelationshipId);
                    }
                }
            }
        }
        // The batch-level signal. Debug-level per-fact logging tells you WHY once you are already
        // looking; this is what makes you look. Warned once per batch rather than per fact so a large
        // ingestion cannot bury it, and only when the option was actually asked for -- a warning on a
        // feature nobody enabled is how warnings stop being read.
        if (_options.SupersedeReplacedFacts && supersessionEligible == 0 && supersessionRefusals > 0)
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

        return new PersistenceResult
        {
            EntityCount = persistedEntityIds.Count,
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

    private sealed record PreparedEmbeddings(
        IReadOnlyDictionary<string, Entity> Entities,
        IReadOnlyList<PreparedFact> Facts,
        IReadOnlyList<PreparedPreference> Preferences,
        IReadOnlyList<IngestionItemOutcome> Outcomes);

    private sealed record PreparedFact(ExtractedFact Item, float[] Embedding);

    /// <summary>
    /// I-5: the name the user gave. This extraction's naming fact wins (the latest one in it); otherwise
    /// the owner's latest live stored one; otherwise none, and nothing is rewritten.
    /// </summary>
    private async Task<string?> UserNameAsync(
        IReadOnlyList<PreparedFact> facts, string ownerId, CancellationToken cancellationToken)
    {
        var stated = facts.Select(f => f.Item).LastOrDefault(UserNames.IsNamingFact)?.Object;
        if (!string.IsNullOrWhiteSpace(stated)) return stated.Trim();

        // Best-effort: a failed lookup leaves the facts under the words used, it never fails the persist.
        try
        {
            var stored = await _factRepository.FindLatestObjectAsync(
                UserNames.SelfWords, UserNames.NamingPredicates, MemoryScope.For(ownerId, includeShared: false),
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

    /// <summary>I-5: the words that mean the user, and the fact that names them.</summary>
    internal static class UserNames
    {
        /// <summary>Words that can mean the speaker as a subject.</summary>
        internal static readonly string[] SelfWords = ["user", "the user", "i", "me", "myself"];

        /// <summary>
        /// Words that mean the speaker as a fact's OBJECT. Not "I"/"me": as an object they are as often
        /// something else ("lives in | ME", Maine), which a fact's free-text object cannot tell apart.
        /// </summary>
        private static readonly HashSet<string> UserAsObject = new(StringComparer.Ordinal) { "user", "the user" };

        private static readonly HashSet<string> Self = new(SelfWords, StringComparer.Ordinal);

        internal static readonly string[] NamingPredicates =
            ["is named", "name is", "has name", "named", "is called", "goes by", "has the name"];

        private static readonly HashSet<string> Naming = new(NamingPredicates, StringComparer.Ordinal);

        internal static bool IsSelf(string? value) => Self.Contains(MemoryTripleCanonicalizer.CanonicalValue(value));

        /// <summary>
        /// I-7: whether a relationship endpoint is the user: any self word, at either end. Unlike a fact's
        /// object, an endpoint is only ever asked about when it is not an extracted entity, so "ME" the
        /// state would have resolved as one, and "Fabrikam EMPLOYS me" is the user.
        /// </summary>
        internal static bool MeansUserEndpoint(string? endpoint, bool source) => IsSelf(endpoint);

        internal static bool IsNamingPredicate(string? predicate) => Naming.Contains(MemoryTripleCanonicalizer.Canonical(predicate));

        internal static bool IsNamingFact(ExtractedFact fact) => IsSelf(fact.Subject) && IsNamingPredicate(fact.Predicate);

        /// <summary>
        /// Whether this slot of the fact is the user and may be stored under their name: never in the
        /// naming fact itself, never in what the assistant said about itself ("I | recommend | …"), and
        /// as an object only "user" / "the user", and only when the subject is not itself the user.
        /// </summary>
        internal static bool MeansUser(ExtractedFact fact, bool subject)
        {
            if (IsNamingFact(fact)) return false;
            if (string.Equals(fact.SourceRole, "assistant", StringComparison.OrdinalIgnoreCase)) return false;
            return subject
                ? IsSelf(fact.Subject)
                : UserAsObject.Contains(MemoryTripleCanonicalizer.CanonicalValue(fact.Object)) && !IsSelf(fact.Subject);
        }
    }

    /// <summary>
    /// I-6: one fact per statement within an extraction. Two facts are the same statement in other words
    /// when they are at least <paramref name="threshold"/> similar AND <see cref="MayBeOneStatement"/>:
    /// similarity alone scores "has 2 kids" / "has 3 kids" and "is vegetarian" / "is not vegetarian" as
    /// near-identical. The kept phrasing is the user's over the assistant's, then the more confident, then
    /// the first; it takes over a validity window or source turn only the other had, unless only the
    /// assistant said it.
    /// Each dropped fact gets a <see cref="IngestionItemStatus.Skipped"/> outcome naming the kept one, and
    /// the span records how many were merged (<c>memory.persist.facts_merged</c>).
    /// </summary>
    private static (IReadOnlyList<PreparedFact> Kept, IReadOnlyList<IngestionItemOutcome> Merged) WithoutNearDuplicates(
        IReadOnlyList<PreparedFact> facts, double threshold)
    {
        var kept = new List<PreparedFact>(facts.Count);
        var merged = new List<IngestionItemOutcome>();
        foreach (var fact in facts)
        {
            var twin = fact.Embedding is { Length: > 0 }
                ? kept.FindIndex(k => k.Embedding is { Length: > 0 } && k.Embedding.Length == fact.Embedding.Length &&
                                      MayBeOneStatement(k.Item, fact.Item) &&
                                      Resolution.SemanticMatchEntityMatcher.CosineSimilarity(k.Embedding, fact.Embedding) >= threshold)
                : -1;
            if (twin < 0)
            {
                kept.Add(fact);
                continue;
            }
            var (winner, loser) = Prefer(fact.Item, kept[twin].Item) ? (fact, kept[twin]) : (kept[twin], fact);
            // What only the assistant said is not carried into the user's words.
            var carry = !IsAssistant(loser.Item);
            // At the place of its latest telling (this one): what was said last is what the person holds now.
            kept.RemoveAt(twin);
            kept.Add(winner with
            {
                Item = winner.Item with
                {
                    ValidFrom = winner.Item.ValidFrom ?? (carry ? loser.Item.ValidFrom : null),
                    // A date and its precision travel together: from whichever side the date came.
                    ValidFromPrecision = winner.Item.ValidFrom is not null ? winner.Item.ValidFromPrecision
                        : carry ? loser.Item.ValidFromPrecision : DatePrecision.Unspecified,
                    ValidUntil = winner.Item.ValidUntil ?? (carry ? loser.Item.ValidUntil : null),
                    ValidUntilPrecision = winner.Item.ValidUntil is not null ? winner.Item.ValidUntilPrecision
                        : carry ? loser.Item.ValidUntilPrecision : DatePrecision.Unspecified,
                    OccurredOn = winner.Item.OccurredOn ?? (carry ? loser.Item.OccurredOn : null),
                    OccurredOnPrecision = winner.Item.OccurredOn is not null ? winner.Item.OccurredOnPrecision
                        : carry ? loser.Item.OccurredOnPrecision : DatePrecision.Unspecified,
                    SourceTurn = winner.Item.SourceTurn ?? (carry ? loser.Item.SourceTurn : null),
                    Replaces = winner.Item.Replaces ?? (carry ? loser.Item.Replaces : null),
                },
            });
            merged.Add(new IngestionItemOutcome
            {
                Kind = MemoryItemKind.Fact,
                Stage = IngestionStage.Persistence,
                Status = IngestionItemStatus.Skipped,
                SourceKey = $"{loser.Item.Subject} {loser.Item.Predicate} {loser.Item.Object}",
                ErrorCode = MemoryErrorCodes.FactMergedWithinExtraction,
                ErrorMessage = $"Same statement as '{winner.Item.Subject} {winner.Item.Predicate} {winner.Item.Object}' in this extraction.",
            });
        }
        if (merged.Count > 0)
            System.Diagnostics.Activity.Current?.SetTag("memory.persist.facts_merged", merged.Count);
        return (kept, merged);

        // The user's own words over the assistant's paraphrase of them (what memory records is what the
        // user said), then confidence; the earlier phrasing on a tie.
        static bool Prefer(ExtractedFact candidate, ExtractedFact current)
        {
            if (IsAssistant(candidate) != IsAssistant(current)) return IsAssistant(current);
            return candidate.Confidence > current.Confidence;
        }

        static bool IsAssistant(ExtractedFact fact) =>
            string.Equals(fact.SourceRole, "assistant", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly HashSet<string> NegationWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "not", "no", "never", "none", "nothing", "nobody", "neither", "nor", "without", "cannot", "non",
    };

    /// <summary>
    /// Whether two facts CAN be one statement phrased twice, whatever their similarity: the same subject,
    /// only one of predicate and object worded differently, the same numbers, the same negations, and no
    /// conflicting validity window. Conservative on purpose: a missed merge leaves a duplicate line, a wrong
    /// one loses a fact.
    /// </summary>
    internal static bool MayBeOneStatement(ExtractedFact left, ExtractedFact right)
    {
        static string Key(string value) => MemoryTripleCanonicalizer.CanonicalValue(value);
        // The fact that names the user is how the name is found in every later session: never merged away.
        if (UserNames.IsNamingFact(left) || UserNames.IsNamingFact(right)) return false;
        if (Key(left.Subject) != Key(right.Subject)) return false;
        if (Key(left.Predicate) != Key(right.Predicate) && Key(left.Object) != Key(right.Object)) return false;
        if (Numbers(left) != Numbers(right) || Negations(left) != Negations(right)) return false;
        return Agree(left.ValidFrom, right.ValidFrom) && Agree(left.ValidUntil, right.ValidUntil) &&
               Agree(left.OccurredOn, right.OccurredOn);

        static bool Agree(DateTimeOffset? a, DateTimeOffset? b) => a is null || b is null || a == b;

        static string Numbers(ExtractedFact fact) => string.Join(
            ",", System.Text.RegularExpressions.Regex.Matches($"{fact.Predicate} {fact.Object}", "[0-9]+").Select(m => m.Value));

        static string Negations(ExtractedFact fact) => string.Join(
            ",", System.Text.RegularExpressions.Regex.Split(
                    $"{fact.Predicate} {fact.Object}".Replace("n't", " not", StringComparison.OrdinalIgnoreCase), "[^A-Za-z]+")
                .Where(NegationWords.Contains)
                .Select(word => word.ToLowerInvariant())
                .Order(StringComparer.Ordinal));
    }

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
