using static AgentMemory.Core.Extraction.PersistenceOutcomes;
using static AgentMemory.Core.Extraction.PersistenceMetadata;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Core.Extraction;

/// <summary>Persists entities and builds name/alias resolution for later fact and relationship writes.</summary>
internal sealed class EntityPersistence
{
    private readonly IEntityRepository _entityRepository;
    private readonly IEmbeddingOrchestrator _embeddingOrchestrator;
    private readonly ExtractionOptions _options;
    private readonly ILogger _logger;

    public EntityPersistence(IEntityRepository entityRepository, IEmbeddingOrchestrator embeddingOrchestrator,
        ExtractionOptions options, ILogger logger)
    {
        _entityRepository = entityRepository;
        _embeddingOrchestrator = embeddingOrchestrator;
        _options = options;
        _logger = logger;
    }

    public async Task<EntityPersistenceResult> PersistAsync(
        IReadOnlyDictionary<string, Entity> entities,
        string? userName,
        string? ownerId,
        MemoryTrustLevel trustLevel,
        IReadOnlyList<string> sourceMessageIds,
        List<IngestionItemOutcome> outcomes,
        bool failFast,
        CancellationToken cancellationToken = default)
    {
        var persistedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
        var persistedEntityIds = new HashSet<string>(StringComparer.Ordinal);
        // 36.6 (D-6). The speaker is not an entity called "user": the prompt calls the speaker "the user", and the
        // model lists "user" among the people (found live: a "user" node beside the person's own, with relationships
        // hanging from it). Once the user's name is known it is not stored, and a relationship from a self word lands
        // on the person's own entity (I-7). Until then it is stored as before, so no relationship is lost.
        // With the name known, the speaker's entity IS the named person: written under that name (so a relationship
        // from "user" lands on it), or, when that person is already an entity here or in the store, not written at all
        // (I-7 then finds that one). Never a node called "user".
        var speakerIsKnown = false;
        if (userName is not null && entities.Keys.Any(MemoryUserNames.IsSelf))
        {
            speakerIsKnown = entities.Keys.Any(key => string.Equals(key, userName, StringComparison.OrdinalIgnoreCase));
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
        var speakerKey = userName is null ? null : entities.Keys.FirstOrDefault(MemoryUserNames.IsSelf);
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
        var entityInputs = entities
            .Where(pair => userName is null || !MemoryUserNames.IsSelf(pair.Key) || (!speakerIsKnown && pair.Key == speakerKey))
            .Select(pair => userName is not null && MemoryUserNames.IsSelf(pair.Key)
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

        return new EntityPersistenceResult
        {
            ByName = persistedEntityMap,
            // Aliases add map entries, not additional persisted entities.
            Count = persistedEntityIds.Count,
        };
    }
}

internal sealed record EntityPersistenceResult
{
    public required IReadOnlyDictionary<string, Entity> ByName { get; init; }
    public required int Count { get; init; }
}
