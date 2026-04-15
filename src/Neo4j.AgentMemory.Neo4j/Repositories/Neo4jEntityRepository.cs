using System.Text.Json;
using Microsoft.Extensions.Logging;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Repositories;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
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

        const string cypher = @"
            MERGE (e:Entity {id: $id})
            ON CREATE SET
                e.name               = $name,
                e.canonical_name     = $canonicalName,
                e.type               = $type,
                e.subtype            = $subtype,
                e.description        = $description,
                e.confidence         = $confidence,
                e.aliases            = $aliases,
                e.attributes         = $attributes,
                e.source_message_ids = $sourceMessageIds,
                e.created_at         = datetime($createdAtUtc),
                e.metadata           = $metadata
            ON MATCH SET
                e.name               = $name,
                e.canonical_name     = $canonicalName,
                e.type               = $type,
                e.subtype            = $subtype,
                e.description        = $description,
                e.confidence         = $confidence,
                e.aliases            = $aliases,
                e.attributes         = $attributes,
                e.source_message_ids = $sourceMessageIds,
                e.metadata           = $metadata,
                e.updated_at         = datetime()
            RETURN e";

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

            var cursor = await runner.RunAsync(cypher, parameters);
            var records = await cursor.ToListAsync();
            var node = records.Count > 0 ? records[0]["e"].As<INode>() : null;

            // Persist geospatial location if provided
            if (entity.Latitude.HasValue && entity.Longitude.HasValue)
            {
                await runner.RunAsync(
                    "MATCH (e:Entity {id: $id}) SET e.location = point({latitude: $lat, longitude: $lon})",
                    new { id = entity.EntityId, lat = entity.Latitude.Value, lon = entity.Longitude.Value });
            }

            if (entity.Embedding is not null)
            {
                await runner.RunAsync(
                    "MATCH (e:Entity {id: $id}) SET e.embedding = $embedding",
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
                await runner.RunAsync(@"
                    MATCH (e:Entity {id: $id})
                    UNWIND $sourceMessageIds AS msgId
                    MATCH (m:Message {id: msgId})
                    MERGE (e)-[:EXTRACTED_FROM]->(m)",
                    new { id = entity.EntityId, sourceMessageIds = entity.SourceMessageIds.ToList() });
            }

            return node is not null ? MapToEntity(node, entity.Embedding) : entity;
        }, cancellationToken);
    }

    public async Task<Entity?> GetByIdAsync(string entityId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting entity {Id}", entityId);

        const string cypher = "MATCH (e:Entity {id: $id}) RETURN e";

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, new { id = entityId });
            var records = await cursor.ToListAsync();
            if (records.Count == 0) return null;
            var node = records[0]["e"].As<INode>();
            return MapToEntity(node, ReadEmbedding(node));
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<Entity>> GetByNameAsync(string name, bool includeAliases = true, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting entities by name '{Name}', includeAliases={IncludeAliases}", name, includeAliases);

        var cypher = includeAliases
            ? "MATCH (e:Entity) WHERE e.name = $name OR $name IN e.aliases RETURN e"
            : "MATCH (e:Entity {name: $name}) RETURN e";

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

        const string cypher = @"
            CALL db.index.vector.queryNodes('entity_embedding_idx', $limit, $embedding)
            YIELD node, score
            WHERE score >= $minScore
            RETURN node, score
            ORDER BY score DESC";

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, new
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

        const string cypher = "MATCH (e:Entity {type: $type}) RETURN e";

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, new { type });
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

        var cypher = type is null
            ? "MATCH (e:Entity) WHERE toLower(e.name) CONTAINS toLower($name) OR toLower(e.canonical_name) CONTAINS toLower($name) RETURN e"
            : "MATCH (e:Entity {type: $type}) WHERE toLower(e.name) CONTAINS toLower($name) OR toLower(e.canonical_name) CONTAINS toLower($name) RETURN e";

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

    public async Task AddMentionAsync(string messageId, string entityId, double? confidence = null, int? startPos = null, int? endPos = null, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Adding MENTIONS: Message {MessageId} -> Entity {EntityId}", messageId, entityId);

        const string cypher = @"
            MATCH (m:Message {id: $messageId})
            MATCH (e:Entity {id: $entityId})
            MERGE (m)-[r:MENTIONS]->(e)
            ON CREATE SET r.confidence = $confidence, r.start_pos = $startPos, r.end_pos = $endPos";

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(cypher, new { messageId, entityId, confidence = (object?)confidence, startPos = (object?)startPos, endPos = (object?)endPos });
        }, cancellationToken);
    }

    public async Task AddMentionsBatchAsync(string messageId, IReadOnlyList<string> entityIds, double? confidence = null, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Adding {Count} MENTIONS for Message {MessageId}", entityIds.Count, messageId);

        const string cypher = @"
            MATCH (m:Message {id: $messageId})
            UNWIND $entityIds AS eid
            MATCH (e:Entity {id: eid})
            MERGE (m)-[r:MENTIONS]->(e)
            ON CREATE SET r.confidence = $confidence";

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(cypher, new { messageId, entityIds = entityIds.ToList(), confidence = (object?)confidence });
        }, cancellationToken);
    }

    public async Task AddSameAsRelationshipAsync(string entityId1, string entityId2, double confidence, string matchType, string status = "pending", CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Adding SAME_AS: {EntityId1} <-> {EntityId2} (confidence={Confidence}, matchType={MatchType})",
            entityId1, entityId2, confidence, matchType);

        const string cypher = @"
            MATCH (e1:Entity {id: $entityId1})
            MATCH (e2:Entity {id: $entityId2})
            MERGE (e1)-[r:SAME_AS]->(e2)
            ON CREATE SET r.confidence = $confidence, r.match_type = $matchType, r.created_at = datetime(), r.status = $status
            ON MATCH SET r.confidence = CASE WHEN $confidence > r.confidence THEN $confidence ELSE r.confidence END, r.updated_at = datetime()";

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(cypher, new { entityId1, entityId2, confidence, matchType, status });
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<(Entity Entity, double Confidence, string MatchType)>> GetSameAsEntitiesAsync(string entityId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting SAME_AS entities for {EntityId}", entityId);

        const string cypher = @"
            MATCH (e:Entity {id: $entityId})-[r:SAME_AS]-(other:Entity)
            RETURN other, r.confidence AS confidence, r.match_type AS matchType";

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, new { entityId });
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

        const string mergeCypher = @"
            UNWIND $items AS item
            MERGE (e:Entity {id: item.id})
            ON CREATE SET
                e.name               = item.name,
                e.canonical_name     = item.canonical_name,
                e.type               = item.type,
                e.subtype            = item.subtype,
                e.description        = item.description,
                e.confidence         = item.confidence,
                e.aliases            = item.aliases,
                e.attributes         = item.attributes,
                e.source_message_ids = item.source_message_ids,
                e.created_at         = datetime(item.created_at),
                e.metadata           = item.metadata
            ON MATCH SET
                e.name               = item.name,
                e.canonical_name     = item.canonical_name,
                e.type               = item.type,
                e.subtype            = item.subtype,
                e.description        = item.description,
                e.confidence         = item.confidence,
                e.aliases            = item.aliases,
                e.attributes         = item.attributes,
                e.source_message_ids = item.source_message_ids,
                e.metadata           = item.metadata,
                e.updated_at         = datetime()
            RETURN e";

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
            var cursor = await runner.RunAsync(mergeCypher, new { items });
            var records = await cursor.ToListAsync();

            // Set embeddings individually
            foreach (var entity in entities.Where(e => e.Embedding is not null))
            {
                await runner.RunAsync(
                    "MATCH (e:Entity {id: $id}) SET e.embedding = $embedding",
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
                await runner.RunAsync(@"
                    MATCH (e:Entity {id: $id})
                    UNWIND $sourceMessageIds AS msgId
                    MATCH (m:Message {id: msgId})
                    MERGE (e)-[:EXTRACTED_FROM]->(m)",
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
            await runner.RunAsync(@"
                MATCH (e:Entity {id: $entityId}), (m:Message {id: $messageId})
                MERGE (e)-[r:EXTRACTED_FROM]->(m)
                ON CREATE SET r.confidence = $confidence, r.start_pos = $startPos, r.end_pos = $endPos, r.context = $context, r.created_at = datetime()
                ON MATCH SET r.confidence = CASE WHEN $confidence IS NOT NULL AND ($confidence > r.confidence OR r.confidence IS NULL) THEN $confidence ELSE r.confidence END",
                new { entityId, messageId, confidence = (object?)confidence, startPos = (object?)startPos, endPos = (object?)endPos, context = (object?)context });
        }, cancellationToken);
    }

    public async Task MergeEntitiesAsync(string sourceEntityId, string targetEntityId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Merging entity {SourceId} into {TargetId}", sourceEntityId, targetEntityId);

        const string cypher = @"
            MATCH (source:Entity {id: $sourceEntityId})
            MATCH (target:Entity {id: $targetEntityId})
            CALL (source, target) {
                MATCH (source)<-[:MENTIONS]-(m:Message)
                WHERE NOT (m)-[:MENTIONS]->(target)
                MERGE (m)-[:MENTIONS]->(target)
                RETURN count(*) AS mentionsTransferred
            }
            CALL (source, target) {
                MATCH (source)-[r:SAME_AS]-(other:Entity)
                WHERE other <> target AND NOT (target)-[:SAME_AS]-(other)
                MERGE (target)-[:SAME_AS {confidence: r.confidence, match_type: r.match_type, created_at: datetime()}]-(other)
                RETURN count(*) AS sameAsTransferred
            }
            SET source.merged_into = target.id, source.merged_at = datetime()
            WITH source, target,
                 coalesce(target.aliases, []) +
                 [x IN ([source.name] + coalesce(source.aliases, []))
                  WHERE NOT x IN coalesce(target.aliases, [])] AS mergedAliases
            SET target.aliases = mergedAliases,
                target.description = CASE
                    WHEN target.description IS NULL THEN source.description
                    WHEN source.description IS NULL OR target.description CONTAINS source.description THEN target.description
                    ELSE target.description + ' ' + source.description
                END,
                target.embedding = null,
                target.updated_at = datetime()
            RETURN source, target";

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(cypher, new { sourceEntityId, targetEntityId });
        }, cancellationToken);

        await RefreshEntitySearchFieldsAsync(targetEntityId, cancellationToken);
    }

    public async Task RefreshEntitySearchFieldsAsync(string entityId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Refreshing search fields for entity {Id}", entityId);

        const string cypher = @"
            MATCH (e:Entity {id: $entityId})
            SET e.updated_at = datetime($updatedAt),
                e.aliases    = [x IN coalesce(e.aliases, []) WHERE x IS NOT NULL AND size(toString(x)) > 0]
            RETURN e";

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(cypher, new
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

        const string cypher = @"
            MATCH (e:Entity)
            WHERE e.location IS NOT NULL
              AND point.distance(e.location, point({latitude: $lat, longitude: $lon})) < $radiusMeters
            RETURN e
            ORDER BY point.distance(e.location, point({latitude: $lat, longitude: $lon}))
            LIMIT $limit";

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, new
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

        const string cypher = @"
            MATCH (e:Entity)
            WHERE e.location IS NOT NULL
              AND point.withinBBox(
                    e.location,
                    point({longitude: $minLon, latitude: $minLat}),
                    point({longitude: $maxLon, latitude: $maxLat}))
            RETURN e
            LIMIT $limit";

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, new { minLat, minLon, maxLat, maxLon, limit });
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

        const string cypher = "MATCH (e:Entity) WHERE e.embedding IS NULL RETURN e LIMIT $limit";

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, new { limit });
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
                "MATCH (e:Entity {id: $id}) SET e.embedding = $embedding",
                new { id = entityId, embedding = embedding.ToList() });
        }, cancellationToken);
    }

    public async Task<bool> DeleteAsync(string entityId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Deleting entity {Id}", entityId);

        const string cypher = @"
            MATCH (e:Entity {id: $entityId})
            DETACH DELETE e
            RETURN count(e) > 0 AS deleted";

        return await _tx.WriteAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, new { entityId });
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