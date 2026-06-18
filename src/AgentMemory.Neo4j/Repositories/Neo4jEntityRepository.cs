using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using Neo4j.Driver;
using static AgentMemory.Neo4j.Repositories.Neo4jRecordMapper;

namespace AgentMemory.Neo4j.Repositories;

public sealed class Neo4jEntityRepository : IEntityRepository
{
    private const int OwnerOverFetchFactor = Neo4jFactRepository.OwnerOverFetchFactor;
    private const int OwnerOverFetchFloor = Neo4jFactRepository.OwnerOverFetchFloor;

    private readonly INeo4jTransactionRunner _tx;
    private readonly ILogger<Neo4jEntityRepository> _logger;
    private readonly MemoryRankingOptions _ranking;
    private readonly MemoryDecayOptions _decay;
    private readonly IMemoryRankingContext? _rankingContext;

    public Neo4jEntityRepository(
        INeo4jTransactionRunner tx,
        ILogger<Neo4jEntityRepository> logger,
        IOptions<MemoryRankingOptions>? ranking = null,
        IOptions<MemoryDecayOptions>? decay = null,
        IMemoryRankingContext? rankingContext = null)
    {
        _tx = tx;
        _logger = logger;
        _ranking = ranking?.Value ?? MemoryRankingOptions.Default;
        _decay = decay?.Value ?? MemoryDecayOptions.Default;
        _rankingContext = rankingContext;
    }

    public async Task<Entity> UpsertAsync(Entity entity, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Upserting entity {Id} ({Name})", entity.EntityId, entity.Name);

        return await _tx.WriteAsync(async runner =>
        {
            var parameters = new Dictionary<string, object?>
            {
                ["id"]             = entity.EntityId,
                ["ownerId"]        = entity.OwnerId,
                ["name"]           = entity.Name,
                ["canonicalName"]  = (object?)entity.CanonicalName,
                ["type"]           = entity.Type,
                ["subtype"]        = (object?)entity.Subtype,
                ["description"]    = (object?)entity.Description,
                ["confidence"]     = entity.Confidence,
                ["aliases"]        = entity.Aliases.ToList(),
                ["attributes"]     = SerializeMetadata(entity.Attributes),
                ["sourceMessageIds"] = entity.SourceMessageIds.ToList(),
                ["createdAtUtc"]   = entity.CreatedAtUtc.ToString("O"),
                ["metadata"]       = SerializeMetadata(entity.Metadata)
            };

            var cursor = await runner.RunAsync(EntityQueries.Upsert, parameters);
            var records = await cursor.ToListAsync();
            var node = records.Count > 0 ? records[0]["e"].As<INode>() : null;

            // Persist geospatial location if provided
            if (entity.Latitude.HasValue && entity.Longitude.HasValue)
            {
                await runner.RunAsync(
                    SharedFragments.SetEntityLocation,
                    new { id = entity.EntityId, lat = entity.Latitude.Value, lon = entity.Longitude.Value });
            }

            // Only persist a real (non-empty) vector. A zero-length embedding (the orchestrator's
            // degraded result on a generation failure) must leave the `embedding` property NULL so the
            // back-fill job can later re-process the node — writing `[]` would make `embedding IS NULL`
            // false and strand the node un-searchable forever.
            if (entity.Embedding is { Length: > 0 })
            {
                await runner.RunAsync(
                    SharedFragments.SetEntityEmbedding,
                    new { id = entity.EntityId, embedding = entity.Embedding.ToList() });
            }

            // Dynamically add POLE+O type labels
            var labels = BuildDynamicLabels(entity.Type, entity.Subtype);
            if (labels.Count > 0)
            {
                var labelClause = string.Join(", ", labels.Select(l => $"e:{SanitizeLabel(l)}"));
                await runner.RunAsync($"MATCH (e:Entity {{id: $id}}) SET {labelClause}", new { id = entity.EntityId });
            }

            // Auto-create EXTRACTED_FROM relationships for all source messages
            if (entity.SourceMessageIds.Count > 0)
            {
                await runner.RunAsync(
                    SharedFragments.LinkEntityExtractedFrom,
                    new { id = entity.EntityId, sourceMessageIds = entity.SourceMessageIds.ToList() });
            }

            // The `node` was captured from the MERGE BEFORE the geospatial location was written in a
            // separate query, so MapToEntity finds no `location` property and yields null coords. Carry the
            // caller-supplied (and now-persisted) coordinates onto the returned object so it matches DB state.
            return node is not null
                ? MapToEntity(node, entity.Embedding) with { Latitude = entity.Latitude, Longitude = entity.Longitude }
                : entity;
        }, cancellationToken);
    }

    public async Task<Entity?> GetByIdAsync(string entityId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting entity {Id}", entityId);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(EntityQueries.GetById, new { id = entityId });
            var records = await cursor.ToListAsync();
            if (records.Count == 0) return null;
            var node = records[0]["e"].As<INode>();
            return MapToEntity(node, ReadEmbedding(node));
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<Entity>> GetByNameAsync(
        string name, bool includeAliases = true, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        _logger.LogDebug("Getting entities by name '{Name}', includeAliases={IncludeAliases}, owner={Owner}",
            name, includeAliases, scope?.OwnerId);

        var cypher = EntityQueries.GetByName(includeAliases, hasOwner, includeShared);
        var parameters = new Dictionary<string, object?> { ["name"] = name };
        if (hasOwner) parameters["ownerId"] = scope!.OwnerId;

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, parameters);
            var records = await cursor.ToListAsync();
            return records.Select(r =>
            {
                var node = r["e"].As<INode>();
                return MapToEntity(node, ReadEmbedding(node));
            }).ToList();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<(Entity Entity, double Score)>> SearchByVectorAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        int topK = hasOwner ? Math.Max(limit * OwnerOverFetchFactor, limit + OwnerOverFetchFloor) : limit;
        _logger.LogDebug("Vector search entities, limit={Limit}, owner={Owner}", limit, scope?.OwnerId);

        var ranking = _rankingContext?.Current ?? _ranking;   // per-request intent (D3) overrides the configured ranking
        bool recencyRerank = ranking.RecencyRerankEnabled;
        var cypher = EntityQueries.SearchByVector(hasOwner, includeShared, topK, recencyRerank);
        var parameters = new Dictionary<string, object?>
        {
            ["embedding"] = queryEmbedding.ToList(),
            ["limit"] = limit,
            ["minScore"] = minScore,
        };
        if (hasOwner) parameters["ownerId"] = scope!.OwnerId;
        if (recencyRerank) RerankParameters.Add(parameters, ranking, _decay);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, parameters);
            var records = await cursor.ToListAsync();
            return records.Select(r =>
            {
                var node  = r["node"].As<INode>();
                var score = r["score"].As<double>();
                return (MapToEntity(node, ReadEmbedding(node)), score);
            }).ToList();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<Entity>> GetByTypeAsync(string type, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        _logger.LogDebug("Getting entities by type {Type}, owner={Owner}", type, scope?.OwnerId);

        var cypher = EntityQueries.GetByType(hasOwner, includeShared);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = hasOwner
                ? await runner.RunAsync(cypher, new Dictionary<string, object> { ["type"] = type, ["ownerId"] = scope!.OwnerId! })
                : await runner.RunAsync(cypher, new { type });
            var records = await cursor.ToListAsync();
            return records.Select(r =>
            {
                var node = r["e"].As<INode>();
                return MapToEntity(node, ReadEmbedding(node));
            }).ToList();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<Entity>> SearchByNameAsync(string name, string? type = null, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        _logger.LogDebug("Searching entities by name '{Name}', type={Type}, owner={Owner}", name, type, scope?.OwnerId);

        var cypher = EntityQueries.SearchByNameFiltered(type, hasOwner, includeShared);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = hasOwner
                ? await runner.RunAsync(cypher, new Dictionary<string, object?> { ["name"] = name, ["type"] = type, ["ownerId"] = scope!.OwnerId })
                : await runner.RunAsync(cypher, new { name, type });
            var records = await cursor.ToListAsync();
            return records.Select(r =>
            {
                var node = r["e"].As<INode>();
                return MapToEntity(node, ReadEmbedding(node));
            }).ToList();
        }, cancellationToken);
    }

    public async Task AddMentionAsync(string messageId, string entityId, double? confidence = null, int? startPos = null, int? endPos = null, string? context = null, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Adding MENTIONS: Message {MessageId} -> Entity {EntityId}", messageId, entityId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(EntityQueries.AddMention, new { messageId, entityId, confidence = (object?)confidence, startPos = (object?)startPos, endPos = (object?)endPos, context = (object?)context });
        }, cancellationToken);
    }

    public async Task AddMentionsBatchAsync(string messageId, IReadOnlyList<string> entityIds, double? confidence = null, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Adding {Count} MENTIONS for Message {MessageId}", entityIds.Count, messageId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(EntityQueries.AddMentionsBatch, new { messageId, entityIds = entityIds.ToList(), confidence = (object?)confidence });
        }, cancellationToken);
    }

    public async Task AddSameAsRelationshipAsync(string entityId1, string entityId2, double confidence, string matchType, string status = "pending", CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Adding SAME_AS: {EntityId1} <-> {EntityId2} (confidence={Confidence}, matchType={MatchType})",
            entityId1, entityId2, confidence, matchType);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(EntityQueries.AddSameAs, new { entityId1, entityId2, confidence, matchType, status });
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<(Entity Entity, double Confidence, string MatchType)>> GetSameAsEntitiesAsync(string entityId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting SAME_AS entities for {EntityId}", entityId);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(EntityQueries.GetSameAsEntities, new { entityId });
            var records = await cursor.ToListAsync();
            return records.Select(r =>
            {
                var node       = r["other"].As<INode>();
                var confidence = r["confidence"].As<double>();
                var matchType  = r["matchType"].As<string>();
                return (MapToEntity(node, ReadEmbedding(node)), confidence, matchType);
            }).ToList();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<Entity>> UpsertBatchAsync(IReadOnlyList<Entity> entities, CancellationToken cancellationToken = default)
    {
        if (entities.Count == 0) return Array.Empty<Entity>();

        _logger.LogDebug("Batch upserting {Count} entities", entities.Count);

        var items = entities.Select(e => new Dictionary<string, object?>
        {
            ["id"]                = e.EntityId,
            ["owner_id"]          = e.OwnerId,
            ["name"]              = e.Name,
            ["canonical_name"]    = (object?)e.CanonicalName,
            ["type"]              = e.Type,
            ["subtype"]           = (object?)e.Subtype,
            ["description"]       = (object?)e.Description,
            ["confidence"]        = e.Confidence,
            ["aliases"]           = e.Aliases.ToList(),
            ["attributes"]        = SerializeMetadata(e.Attributes),
            ["source_message_ids"] = e.SourceMessageIds.ToList(),
            ["created_at"]        = e.CreatedAtUtc.ToString("O"),
            ["metadata"]          = SerializeMetadata(e.Metadata)
        }).ToList();

        return await _tx.WriteAsync(async runner =>
        {
            var cursor = await runner.RunAsync(EntityQueries.UpsertBatch, new { items });
            var records = await cursor.ToListAsync();

            // Set embeddings individually — only for nodes with a real (non-empty) vector, so a degraded
            // empty embedding leaves `embedding` NULL and re-queueable for the back-fill (see UpsertAsync).
            foreach (var entity in entities.Where(e => e.Embedding is { Length: > 0 }))
            {
                await runner.RunAsync(
                    SharedFragments.SetEntityEmbedding,
                    new { id = entity.EntityId, embedding = entity.Embedding!.ToList() });
            }

            // Persist geospatial location individually (parity with single UpsertAsync — the UNWIND
            // upsert can't set a point() from per-row nullable coords without erroring on missing ones).
            foreach (var entity in entities.Where(e => e.Latitude.HasValue && e.Longitude.HasValue))
            {
                await runner.RunAsync(
                    SharedFragments.SetEntityLocation,
                    new { id = entity.EntityId, lat = entity.Latitude!.Value, lon = entity.Longitude!.Value });
            }

            // Dynamically add POLE+O type labels
            foreach (var entity in entities)
            {
                var labels = BuildDynamicLabels(entity.Type, entity.Subtype);
                if (labels.Count > 0)
                {
                    var labelClause = string.Join(", ", labels.Select(l => $"e:{SanitizeLabel(l)}"));
                    await runner.RunAsync($"MATCH (e:Entity {{id: $id}}) SET {labelClause}", new { id = entity.EntityId });
                }
            }

            // Auto-create EXTRACTED_FROM relationships
            foreach (var entity in entities.Where(e => e.SourceMessageIds.Count > 0))
            {
                await runner.RunAsync(
                    SharedFragments.LinkEntityExtractedFrom,
                    new { id = entity.EntityId, sourceMessageIds = entity.SourceMessageIds.ToList() });
            }

            // Records were read before embeddings/location were written, so carry the caller-supplied
            // embedding AND coordinates onto each returned object so it reflects persisted state (see UpsertAsync).
            var byId = entities.ToDictionary(e => e.EntityId);
            return records.Select(r =>
            {
                var node = r["e"].As<INode>();
                var id   = node["id"].As<string>();
                if (!byId.TryGetValue(id, out var src))
                    return MapToEntity(node, null);
                return MapToEntity(node, src.Embedding) with { Latitude = src.Latitude, Longitude = src.Longitude };
            }).ToList();
        }, cancellationToken);
    }

    public async Task CreateExtractedFromRelationshipAsync(string entityId, string messageId, double? confidence = null, int? startPos = null, int? endPos = null, string? context = null, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating EXTRACTED_FROM: Entity {EntityId} -> Message {MessageId}", entityId, messageId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                EntityQueries.CreateExtractedFrom,
                new { entityId, messageId, confidence = (object?)confidence, startPos = (object?)startPos, endPos = (object?)endPos, context = (object?)context });
        }, cancellationToken);
    }

    public async Task MergeEntitiesAsync(string sourceEntityId, string targetEntityId, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        _logger.LogDebug("Merging entity {SourceId} into {TargetId}, owner={Owner}", sourceEntityId, targetEntityId, scope?.OwnerId);

        var cypher = EntityQueries.MergeEntities(hasOwner, includeShared);

        await _tx.WriteAsync(async runner =>
        {
            if (hasOwner)
                await runner.RunAsync(cypher, new Dictionary<string, object> { ["sourceEntityId"] = sourceEntityId, ["targetEntityId"] = targetEntityId, ["ownerId"] = scope!.OwnerId! });
            else
                await runner.RunAsync(cypher, new { sourceEntityId, targetEntityId });
        }, cancellationToken);

        await RefreshEntitySearchFieldsAsync(targetEntityId, cancellationToken);
    }

    public async Task RefreshEntitySearchFieldsAsync(string entityId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Refreshing search fields for entity {Id}", entityId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(EntityQueries.RefreshSearchFields, new
            {
                entityId,
                updatedAt = DateTimeOffset.UtcNow.ToString("O")
            });
        }, cancellationToken);
    }

    private static Entity MapToEntity(INode node, float[]? embedding)
    {
        double? latitude = null;
        double? longitude = null;
        if (node.Properties.TryGetValue("location", out var locValue) && locValue is Point pt)
        {
            // WGS-84: X = longitude, Y = latitude
            latitude  = pt.Y;
            longitude = pt.X;
        }

        return new Entity
        {
            EntityId       = node["id"].As<string>(),
            OwnerId        = node.Properties.TryGetValue("owner_id", out var oid) ? oid.As<string>() : null,
            Name           = node["name"].As<string>(),
            CanonicalName  = node.Properties.TryGetValue("canonical_name", out var cn) ? cn.As<string>() : null,
            Type           = node["type"].As<string>(),
            Subtype        = node.Properties.TryGetValue("subtype", out var st) ? st.As<string>() : null,
            Description    = node.Properties.TryGetValue("description", out var desc) ? desc.As<string>() : null,
            Confidence     = node["confidence"].As<double>(),
            Embedding      = embedding,
            Latitude       = latitude,
            Longitude      = longitude,
            Aliases        = node.Properties.TryGetValue("aliases", out var al)
                                ? al.As<IList<object>>().Select(a => a.ToString()!).ToList()
                                : Array.Empty<string>(),
            Attributes     = DeserializeMetadata(node.Properties.TryGetValue("attributes", out var attr) ? attr.As<string>() : null),
            SourceMessageIds = node.Properties.TryGetValue("source_message_ids", out var sm)
                                ? sm.As<IList<object>>().Select(v => v.ToString()!).ToList()
                                : Array.Empty<string>(),
            CreatedAtUtc   = Neo4jDateTimeHelper.ReadDateTimeOffset(node["created_at"]),
            UpdatedAtUtc   = node.Properties.TryGetValue("updated_at", out var ua) && ua is not null
                                ? Neo4jDateTimeHelper.ReadNullableDateTimeOffset(ua)
                                : null,
            Metadata       = DeserializeMetadata(node.Properties.TryGetValue("metadata", out var md) ? md.As<string>() : null)
        };
    }

    public async Task<Entity?> ApplyConfidenceDeltaAsync(
        string entityId, double delta, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;

        _logger.LogDebug("Applying confidence delta {Delta} to entity {Id}, owner={Owner}", delta, entityId, scope?.OwnerId);

        var cypher = EntityQueries.ApplyConfidenceDelta(hasOwner, includeShared);
        var parameters = new Dictionary<string, object> { ["id"] = entityId, ["delta"] = delta };
        if (hasOwner) parameters["ownerId"] = scope!.OwnerId!;

        return await _tx.WriteAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, parameters);
            var records = await cursor.ToListAsync();
            if (records.Count == 0) return null;
            var node = records[0]["e"].As<INode>();
            return MapToEntity(node, ReadEmbedding(node));
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<Entity>> SearchByLocationAsync(
        double latitude,
        double longitude,
        double radiusKm,
        int limit = 10,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        _logger.LogDebug("Searching entities near ({Lat},{Lon}) radius={RadiusKm}km, owner={Owner}", latitude, longitude, radiusKm, scope?.OwnerId);

        var cypher = EntityQueries.SearchByLocation(hasOwner, includeShared);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = hasOwner
                ? await runner.RunAsync(cypher, new Dictionary<string, object>
                {
                    ["lat"] = latitude, ["lon"] = longitude, ["radiusMeters"] = radiusKm * 1000.0, ["limit"] = limit, ["ownerId"] = scope!.OwnerId!,
                })
                : await runner.RunAsync(cypher, new { lat = latitude, lon = longitude, radiusMeters = radiusKm * 1000.0, limit });
            var records = await cursor.ToListAsync();
            return records.Select(r =>
            {
                var node = r["e"].As<INode>();
                return MapToEntity(node, ReadEmbedding(node));
            }).ToList();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<Entity>> SearchInBoundingBoxAsync(
        double minLat,
        double minLon,
        double maxLat,
        double maxLon,
        int limit = 10,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        _logger.LogDebug("Searching entities in bounding box ({MinLat},{MinLon})-({MaxLat},{MaxLon}), owner={Owner}",
            minLat, minLon, maxLat, maxLon, scope?.OwnerId);

        var cypher = EntityQueries.SearchInBoundingBox(hasOwner, includeShared);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = hasOwner
                ? await runner.RunAsync(cypher, new Dictionary<string, object>
                {
                    ["minLat"] = minLat, ["minLon"] = minLon, ["maxLat"] = maxLat, ["maxLon"] = maxLon, ["limit"] = limit, ["ownerId"] = scope!.OwnerId!,
                })
                : await runner.RunAsync(cypher, new { minLat, minLon, maxLat, maxLon, limit });
            var records = await cursor.ToListAsync();
            return records.Select(r =>
            {
                var node = r["e"].As<INode>();
                return MapToEntity(node, ReadEmbedding(node));
            }).ToList();
        }, cancellationToken);
    }

    public async Task<PagedResult<Entity>> GetPageWithoutEmbeddingAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting up to {Limit} entities without embeddings", limit);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(EntityQueries.GetPageWithoutEmbedding, new { limit = limit + 1 });
            var records = await cursor.ToListAsync();
            var items = records.Select(r =>
            {
                var node = r["e"].As<INode>();
                return MapToEntity(node, null);
            }).ToList();
            return PaginationHelper.ApplyPagination(items, limit);
        }, cancellationToken);
    }

    public async Task UpdateEmbeddingAsync(
        string entityId,
        float[] embedding,
        CancellationToken cancellationToken = default)
    {
        // Never overwrite with a zero-length vector (e.g. a back-fill run that itself hit a transient
        // embedding failure). Skipping keeps `embedding` NULL so the node stays re-queueable rather than
        // being poisoned with `[]` and stranded un-searchable.
        if (embedding.Length == 0)
        {
            _logger.LogDebug("Skipping empty embedding update for entity {Id}.", entityId);
            return;
        }

        _logger.LogDebug("Updating embedding for entity {Id}", entityId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                EntityQueries.UpdateEmbedding,
                new { id = entityId, embedding = embedding.ToList() });
        }, cancellationToken);
    }

    public async Task<bool> DeleteAsync(string entityId, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        _logger.LogDebug("Deleting entity {Id}, owner={Owner}", entityId, scope?.OwnerId);

        var cypher = EntityQueries.Delete(hasOwner);

        return await _tx.WriteAsync(async runner =>
        {
            var cursor = hasOwner
                ? await runner.RunAsync(cypher, new Dictionary<string, object> { ["entityId"] = entityId, ["ownerId"] = scope!.OwnerId! })
                : await runner.RunAsync(cypher, new { entityId });
            var records = await cursor.ToListAsync();
            return records.Count > 0 && records[0]["deleted"].As<bool>();
        }, cancellationToken);
    }

    public async Task<bool> InvalidateAsync(string entityId, MemoryScope? scope = null, CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        _logger.LogDebug("Invalidating entity {Id}, owner={Owner}", entityId, scope?.OwnerId);

        var cypher = EntityQueries.Invalidate(hasOwner);
        string now = DateTimeOffset.UtcNow.ToString("O");

        return await _tx.WriteAsync(async runner =>
        {
            var parameters = new Dictionary<string, object?> { ["id"] = entityId, ["now"] = now };
            if (hasOwner) parameters["ownerId"] = scope!.OwnerId;
            var cursor = await runner.RunAsync(cypher, parameters);
            var records = await cursor.ToListAsync();
            return records.Count > 0 && records[0]["invalidated"].As<bool>();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<(Entity Entity, double Similarity)>> FindSimilarByEmbeddingAsync(
        string entityId, double minSimilarity = 0.85, int limit = 10, MemoryScope? scope = null, CancellationToken ct = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        // Over-fetch when scoped so a high-volume foreign owner can't starve the post-filter result set.
        int topK = hasOwner ? Math.Max((limit + 1) * OwnerOverFetchFactor, limit + 1 + OwnerOverFetchFloor) : limit + 1;
        _logger.LogDebug("Finding similar entities for {EntityId}, minSimilarity={MinSimilarity}, limit={Limit}, owner={Owner}",
            entityId, minSimilarity, limit, scope?.OwnerId);

        var cypher = EntityQueries.FindSimilarByEmbedding(hasOwner, includeShared);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = hasOwner
                ? await runner.RunAsync(cypher, new Dictionary<string, object> { ["entityId"] = entityId, ["topK"] = topK, ["minSimilarity"] = minSimilarity, ["limit"] = limit, ["ownerId"] = scope!.OwnerId! })
                : await runner.RunAsync(cypher, new { entityId, topK, minSimilarity, limit });
            var records = await cursor.ToListAsync();
            return records.Select(r =>
            {
                var node = r["node"].As<INode>();
                var score = r["score"].As<double>();
                return (MapToEntity(node, ReadEmbedding(node)), score);
            }).ToList();
        }, ct);
    }

    public async Task<IReadOnlyList<DuplicatePair>> GetPendingDuplicatesAsync(
        int limit = 50, CancellationToken ct = default)
    {
        _logger.LogDebug("Getting pending duplicate pairs, limit={Limit}", limit);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(EntityQueries.GetPendingDuplicates, new { limit });
            var records = await cursor.ToListAsync();
            return records.Select(r =>
            {
                var source = MapToEntity(r["a"].As<INode>(), ReadEmbedding(r["a"].As<INode>()));
                var target = MapToEntity(r["b"].As<INode>(), ReadEmbedding(r["b"].As<INode>()));
                var similarity = r["similarity"].As<double>();
                var status = r["status"].As<string>();
                return new DuplicatePair(source, target, similarity, status);
            }).ToList();
        }, ct);
    }

    public async Task<DeduplicationStats> GetDeduplicationStatsAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("Getting deduplication stats");

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(EntityQueries.GetDeduplicationStats, new { });
            var records = await cursor.ToListAsync();
            if (records.Count == 0)
                return new DeduplicationStats(0, 0, 0, 0);

            var record = records[0];
            return new DeduplicationStats(
                PendingCount: record["pending"].As<int>(),
                ConfirmedCount: record["confirmed"].As<int>(),
                RejectedCount: record["rejected"].As<int>(),
                MergedCount: record["merged"].As<int>());
        }, ct);
    }

    public async Task<IReadOnlyList<Entity>> GetEntitiesFromMessageAsync(
        string messageId, CancellationToken ct = default)
    {
        _logger.LogDebug("Getting entities from message {MessageId}", messageId);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(EntityQueries.GetEntitiesFromMessage, new { messageId });
            var records = await cursor.ToListAsync();
            return records.Select(r =>
            {
                var node = r["e"].As<INode>();
                return MapToEntity(node, ReadEmbedding(node));
            }).ToList();
        }, ct);
    }

    public async Task<IReadOnlyList<(Entity Entity, double Score)>> SearchByVectorAsOfAsync(
        float[] queryEmbedding,
        DateTimeOffset asOf,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        int topK = hasOwner ? Math.Max(limit * Neo4jFactRepository.OwnerOverFetchFactor, limit + Neo4jFactRepository.OwnerOverFetchFloor) : limit;
        _logger.LogDebug("Temporal vector search entities as of {AsOf}, limit={Limit}, owner={Owner}", asOf, limit, scope?.OwnerId);

        var cypher = TemporalQueries.SearchEntitiesAsOf(hasOwner, includeShared, topK);
        var parameters = new Dictionary<string, object?>
        {
            ["embedding"]  = queryEmbedding.ToList(),
            ["limit"]      = limit,
            ["minScore"]   = minScore,
            // D6: entities have only the transaction clock, so the AsOf timestamp binds $systemAsOf.
            ["systemAsOf"] = asOf.UtcDateTime.ToString("O")
        };
        if (hasOwner) parameters["ownerId"] = scope!.OwnerId;

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, parameters);
            var records = await cursor.ToListAsync();
            return records.Select(r =>
            {
                var node  = r["node"].As<INode>();
                var score = r["score"].As<double>();
                return (MapToEntity(node, ReadEmbedding(node)), score);
            }).ToList();
        }, cancellationToken);
    }

    private static float[]? ReadEmbedding(INode node)
    {
        if (!node.Properties.TryGetValue("embedding", out var ev) || ev is null) return null;
        return ev.As<IList<object>>().Select(v => Convert.ToSingle(v)).ToArray();
    }

    private static readonly HashSet<string> ValidEntityLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "PERSON", "OBJECT", "LOCATION", "EVENT", "ORGANIZATION",
        "INDIVIDUAL", "GROUP", "ANIMAL", "VEHICLE", "BUILDING", "LANDMARK",
        "CITY", "COUNTRY", "REGION", "ADDRESS", "COMPANY", "GOVERNMENT",
        "CONFERENCE", "MEETING", "INCIDENT"
    };

    internal static List<string> BuildDynamicLabels(string type, string? subtype)
    {
        var labels = new List<string>();
        var sanitizedType = SanitizeLabel(type);
        if (!string.IsNullOrEmpty(sanitizedType) && ValidEntityLabels.Contains(sanitizedType))
            labels.Add(sanitizedType.ToUpperInvariant());
        if (!string.IsNullOrEmpty(subtype))
        {
            var sanitizedSubtype = SanitizeLabel(subtype);
            if (!string.IsNullOrEmpty(sanitizedSubtype) && ValidEntityLabels.Contains(sanitizedSubtype))
                labels.Add(sanitizedSubtype.ToUpperInvariant());
        }
        return labels;
    }

    internal static string SanitizeLabel(string label)
    {
        return new string(label.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
    }
}