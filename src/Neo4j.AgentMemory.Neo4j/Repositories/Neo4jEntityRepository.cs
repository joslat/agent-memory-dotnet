using System.Text.Json;
using Microsoft.Extensions.Logging;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Repositories;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.AgentMemory.Neo4j.Queries;
using Neo4j.Driver;

namespace Neo4j.AgentMemory.Neo4j.Repositories;

public sealed class Neo4jEntityRepository : IEntityRepository
{
    private readonly INeo4jTransactionRunner _tx;
    private readonly ILogger<Neo4jEntityRepository> _logger;

    public Neo4jEntityRepository(INeo4jTransactionRunner tx, ILogger<Neo4jEntityRepository> logger)
    {
        _tx = tx;
        _logger = logger;
    }

    public async Task<Entity> UpsertAsync(Entity entity, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Upserting entity {Id} ({Name})", entity.EntityId, entity.Name);

        return await _tx.WriteAsync(async runner =>
        {
            var parameters = new Dictionary<string, object?>
            {
                ["id"]             = entity.EntityId,
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

            if (entity.Embedding is not null)
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

            return node is not null ? MapToEntity(node, entity.Embedding) : entity;
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

    public async Task<IReadOnlyList<Entity>> GetByNameAsync(string name, bool includeAliases = true, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting entities by name '{Name}', includeAliases={IncludeAliases}", name, includeAliases);

        var cypher = EntityQueries.GetByName(includeAliases);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, new { name });
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
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Vector search entities, limit={Limit}", limit);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(EntityQueries.SearchByVector, new
            {
                embedding = queryEmbedding.ToList(),
                limit,
                minScore
            });
            var records = await cursor.ToListAsync();
            return records.Select(r =>
            {
                var node  = r["node"].As<INode>();
                var score = r["score"].As<double>();
                return (MapToEntity(node, ReadEmbedding(node)), score);
            }).ToList();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<Entity>> GetByTypeAsync(string type, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting entities by type {Type}", type);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(EntityQueries.GetByType, new { type });
            var records = await cursor.ToListAsync();
            return records.Select(r =>
            {
                var node = r["e"].As<INode>();
                return MapToEntity(node, ReadEmbedding(node));
            }).ToList();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<Entity>> SearchByNameAsync(string name, string? type = null, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Searching entities by name '{Name}', type={Type}", name, type);

        var cypher = EntityQueries.SearchByNameFiltered(type);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, new { name, type });
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

            // Set embeddings individually
            foreach (var entity in entities.Where(e => e.Embedding is not null))
            {
                await runner.RunAsync(
                    SharedFragments.SetEntityEmbedding,
                    new { id = entity.EntityId, embedding = entity.Embedding!.ToList() });
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

            var embeddingMap = entities.ToDictionary(e => e.EntityId, e => e.Embedding);
            return records.Select(r =>
            {
                var node = r["e"].As<INode>();
                var id   = node["id"].As<string>();
                return MapToEntity(node, embeddingMap.TryGetValue(id, out var emb) ? emb : null);
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

    public async Task MergeEntitiesAsync(string sourceEntityId, string targetEntityId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Merging entity {SourceId} into {TargetId}", sourceEntityId, targetEntityId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(EntityQueries.MergeEntities, new { sourceEntityId, targetEntityId });
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
            Metadata       = DeserializeMetadata(node.Properties.TryGetValue("metadata", out var md) ? md.As<string>() : null)
        };
    }

    public async Task<IReadOnlyList<Entity>> SearchByLocationAsync(
        double latitude,
        double longitude,
        double radiusKm,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Searching entities near ({Lat},{Lon}) radius={RadiusKm}km", latitude, longitude, radiusKm);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(EntityQueries.SearchByLocation, new
            {
                lat = latitude,
                lon = longitude,
                radiusMeters = radiusKm * 1000.0,
                limit
            });
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
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Searching entities in bounding box ({MinLat},{MinLon})-({MaxLat},{MaxLon})",
            minLat, minLon, maxLat, maxLon);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(EntityQueries.SearchInBoundingBox, new { minLat, minLon, maxLat, maxLon, limit });
            var records = await cursor.ToListAsync();
            return records.Select(r =>
            {
                var node = r["e"].As<INode>();
                return MapToEntity(node, ReadEmbedding(node));
            }).ToList();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<Entity>> GetPageWithoutEmbeddingAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting up to {Limit} entities without embeddings", limit);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(EntityQueries.GetPageWithoutEmbedding, new { limit });
            var records = await cursor.ToListAsync();
            return records.Select(r =>
            {
                var node = r["e"].As<INode>();
                return MapToEntity(node, null);
            }).ToList();
        }, cancellationToken);
    }

    public async Task UpdateEmbeddingAsync(
        string entityId,
        float[] embedding,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Updating embedding for entity {Id}", entityId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                EntityQueries.UpdateEmbedding,
                new { id = entityId, embedding = embedding.ToList() });
        }, cancellationToken);
    }

    public async Task<bool> DeleteAsync(string entityId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Deleting entity {Id}", entityId);

        return await _tx.WriteAsync(async runner =>
        {
            var cursor = await runner.RunAsync(EntityQueries.Delete, new { entityId });
            var records = await cursor.ToListAsync();
            return records.Count > 0 && records[0]["deleted"].As<bool>();
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

    private static string SerializeMetadata(IReadOnlyDictionary<string, object> metadata)
        => metadata.Count == 0 ? "{}" : JsonSerializer.Serialize(metadata);

    private static IReadOnlyDictionary<string, object> DeserializeMetadata(string? json)
        => string.IsNullOrEmpty(json)
            ? new Dictionary<string, object>()
            : JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();
}