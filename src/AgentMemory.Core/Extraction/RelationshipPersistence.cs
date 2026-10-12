using static AgentMemory.Core.Extraction.PersistenceOutcomes;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Memory;
using AgentMemory.Core.Services;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Core.Extraction;

/// <summary>Resolves relationship endpoints, persists edges and ends replaced single-valued edges.</summary>
internal sealed class RelationshipPersistence
{
    private readonly IEntityRepository _entityRepository;
    private readonly IRelationshipRepository _relationshipRepository;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private readonly ExtractionOptions _options;
    private readonly ILogger _logger;
    private readonly ClosingPipeline _closings;

    public RelationshipPersistence(IEntityRepository entityRepository, IRelationshipRepository relationshipRepository,
        IClock clock, IIdGenerator idGenerator, ExtractionOptions options, ILogger logger, ClosingPipeline closings)
    {
        _entityRepository = entityRepository;
        _relationshipRepository = relationshipRepository;
        _clock = clock;
        _idGenerator = idGenerator;
        _options = options;
        _logger = logger;
        _closings = closings;
    }

    public async Task<RelationshipPersistenceResult> PersistAsync(
        IReadOnlyList<ExtractedRelationship> relationships,
        IReadOnlyDictionary<string, Entity> persistedEntityMap,
        string? userName,
        string? ownerId,
        IReadOnlyList<string> sourceMessageIds,
        List<IngestionItemOutcome> outcomes,
        bool failFast,
        bool writtenByWriter,
        CancellationToken cancellationToken = default)
    {
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
            : MemoryUserNames.MeansUserEndpoint(name, source) ? await UserEntityAsync().ConfigureAwait(false)
            : await KnownEntityAsync(name).ConfigureAwait(false);

        // 40.91. A relationship may name someone met in an earlier message ("Marta Ruiz -[SIBLING_OF]-> Iker" said after
        // Marta was introduced): the owner's live entity with that name or alias, read once per name, best-effort. Before
        // this, such a relationship was skipped as "not persisted" and never reached the graph.
        var knownEntities = new Dictionary<string, Entity?>(StringComparer.OrdinalIgnoreCase);
        async Task<Entity?> KnownEntityAsync(string name)
        {
            if (ownerId is null || string.IsNullOrWhiteSpace(name)) return null;
            if (knownEntities.TryGetValue(name, out var known)) return known;
            try
            {
                known = await _entityRepository.FindLiveByNameAsync(
                    name, type: null, MemoryScope.For(ownerId, includeShared: false), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read the entity named '{Name}' for owner {Owner}; its relationships are skipped.", name, ownerId);
                known = null;
            }
            return knownEntities[name] = known;
        }

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

        // An endpoint looked for in the owner's memory and not found was never resolved (the outcome extraction gives when it
        // has no owner to look in); one the extraction resolved but did not write was not persisted.
        IngestionItemOutcome Unreached(string name, string end, string sourceKey) =>
            knownEntities.ContainsKey(name) && !persistedEntityMap.ContainsKey(name)
                ? new IngestionItemOutcome
                {
                    Kind = MemoryItemKind.Relationship,
                    Stage = IngestionStage.Resolution,
                    Status = IngestionItemStatus.Skipped,
                    SourceKey = sourceKey,
                    ErrorCode = MemoryErrorCodes.RelationshipEndpointUnresolved,
                    ErrorMessage = $"{end} entity '{name}' was not resolved.",
                }
                : new IngestionItemOutcome
                {
                    Kind = MemoryItemKind.Relationship,
                    Stage = IngestionStage.RelationshipPersistence,
                    Status = IngestionItemStatus.Skipped,
                    SourceKey = sourceKey,
                    ErrorCode = MemoryErrorCodes.RelationshipEndpointNotPersisted,
                    ErrorMessage = $"{end} entity '{name}' was not persisted.",
                };

        var relationshipInputs = new List<(Relationship Item, string SourceKey)>(
            relationships.Count);
        foreach (var extracted in relationships)
        {
            var relSourceKey = $"{extracted.SourceEntity}-{extracted.RelationshipType}->{extracted.TargetEntity}";

            if (await EndpointAsync(extracted.SourceEntity, source: true).ConfigureAwait(false) is not { } sourceEntity)
            {
                _logger.LogWarning(
                    "Skipping relationship — source entity '{Source}' was not persisted.",
                    extracted.SourceEntity);
                outcomes.Add(Unreached(extracted.SourceEntity, "Source", relSourceKey));
                continue;
            }

            if (await EndpointAsync(extracted.TargetEntity, source: false).ConfigureAwait(false) is not { } targetEntity)
            {
                _logger.LogWarning(
                    "Skipping relationship — target entity '{Target}' was not persisted.",
                    extracted.TargetEntity);
                outcomes.Add(Unreached(extracted.TargetEntity, "Target", relSourceKey));
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
        void RecordPersistedRelationship(string sourceKey, Relationship persisted, bool created)
        {
            persistedRelationshipIds.Add(persisted.RelationshipId);
            persistedRelCount++;
            RecordSuccess(outcomes, MemoryItemKind.Relationship, sourceKey, persisted.RelationshipId,
                created ? MemoryWriteEffect.Created : MemoryWriteEffect.AlreadyStored);
            _logger.LogDebug("Persisted relationship '{SourceKey}'.", sourceKey);
        }

        async Task PersistRelationshipIndividuallyAsync(Relationship item, string sourceKey)
        {
            try
            {
                var persisted = await _relationshipRepository.UpsertAsync(item, cancellationToken).ConfigureAwait(false);
                RecordPersistedRelationship(sourceKey, persisted, created: persisted.RelationshipId == item.RelationshipId);
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
                    input.SourceKey, batchedRelationshipsById[input.Item.RelationshipId],
                    created: batchedRelationshipsById[input.Item.RelationshipId].RelationshipId == input.Item.RelationshipId);
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
        if (_options.SupersedeReplacedFacts && !writtenByWriter)
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
                        // 40.65. With BitemporalChanges the edge ends when the new one began, as its fact does.
                        var endedAt = _clock.UtcNow;
                        if (_options.BitemporalChanges && item.ValidFrom is { } began && began <= endedAt) endedAt = began;
                        await _closings.ApplyAsync(ClosingProposal.Relationship(previous.RelationshipId, endedAt,
                            endScope, ClosingSource.DeterministicReplacement, item.RelationshipId), cancellationToken)
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

        return new RelationshipPersistenceResult
        {
            Inputs = relationshipInputs,
            PersistedIds = persistedRelationshipIds,
            Count = persistedRelCount,
        };
    }
}

internal sealed record RelationshipPersistenceResult
{
    public required IReadOnlyList<(Relationship Item, string SourceKey)> Inputs { get; init; }
    public required IReadOnlySet<string> PersistedIds { get; init; }
    public required int Count { get; init; }
}
