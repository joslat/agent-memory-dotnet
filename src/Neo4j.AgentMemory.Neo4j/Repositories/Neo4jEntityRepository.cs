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
                e.created_at         = $createdAtUtc,
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
                e.metadata           = $metadata
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
            var record = await cursor.SingleAsync();
            var node = record["e"].As<INode>();

            if (entity.Embedding is not null)
            {
                await runner.RunAsync(
                    "MATCH (e:Entity {id: $id}) SET e.embedding = $embedding",
                    new { id = entity.EntityId, embedding = entity.Embedding.ToList() });
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

            return MapToEntity(node, entity.Embedding);
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

    public async Task AddMentionAsync(string messageId, string entityId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Adding MENTIONS: Message {MessageId} -> Entity {EntityId}", messageId, entityId);

        const string cypher = @"
            MATCH (m:Message {id: $messageId})
            MATCH (e:Entity {id: $entityId})
            MERGE (m)-[:MENTIONS]->(e)";

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(cypher, new { messageId, entityId });
        }, cancellationToken);
    }

    public async Task AddMentionsBatchAsync(string messageId, IReadOnlyList<string> entityIds, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Adding {Count} MENTIONS for Message {MessageId}", entityIds.Count, messageId);

        const string cypher = @"
            MATCH (m:Message {id: $messageId})
            UNWIND $entityIds AS eid
            MATCH (e:Entity {id: eid})
            MERGE (m)-[:MENTIONS]->(e)";

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(cypher, new { messageId, entityIds = entityIds.ToList() });
        }, cancellationToken);
    }

    public async Task AddSameAsRelationshipAsync(string entityId1, string entityId2, double confidence, string matchType, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Adding SAME_AS: {EntityId1} <-> {EntityId2} (confidence={Confidence}, matchType={MatchType})",
            entityId1, entityId2, confidence, matchType);

        const string cypher = @"
            MATCH (e1:Entity {id: $entityId1})
            MATCH (e2:Entity {id: $entityId2})
            MERGE (e1)-[r:SAME_AS]->(e2)
            SET r.confidence = $confidence, r.match_type = $matchType, r.created_at = datetime()";

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(cypher, new { entityId1, entityId2, confidence, matchType });
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
                e.created_at         = item.created_at,
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
                e.metadata           = item.metadata
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

    public async Task CreateExtractedFromRelationshipAsync(string entityId, string messageId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating EXTRACTED_FROM: Entity {EntityId} -> Message {MessageId}", entityId, messageId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(@"
                MATCH (e:Entity {id: $entityId}), (m:Message {id: $messageId})
                MERGE (e)-[:EXTRACTED_FROM]->(m)",
                new { entityId, messageId });
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
            SET target.aliases = CASE
                WHEN target.aliases IS NULL THEN [source.name]
                WHEN NOT source.name IN target.aliases THEN target.aliases + source.name
                ELSE target.aliases
            END
            RETURN source, target";

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(cypher, new { sourceEntityId, targetEntityId });
        }, cancellationToken);
    }

    private static Entity MapToEntity(INode node, float[]? embedding) =>
        new()
        {
            EntityId       = node["id"].As<string>(),
            Name           = node["name"].As<string>(),
            CanonicalName  = node.Properties.TryGetValue("canonical_name", out var cn) ? cn.As<string>() : null,
            Type           = node["type"].As<string>(),
            Subtype        = node.Properties.TryGetValue("subtype", out var st) ? st.As<string>() : null,
            Description    = node.Properties.TryGetValue("description", out var desc) ? desc.As<string>() : null,
            Confidence     = node["confidence"].As<double>(),
            Embedding      = embedding,
            Aliases        = node.Properties.TryGetValue("aliases", out var al)
                                ? al.As<IList<object>>().Select(a => a.ToString()!).ToList()
                                : Array.Empty<string>(),
            Attributes     = DeserializeMetadata(node.Properties.TryGetValue("attributes", out var attr) ? attr.As<string>() : null),
            SourceMessageIds = node.Properties.TryGetValue("source_message_ids", out var sm)
                                ? sm.As<IList<object>>().Select(v => v.ToString()!).ToList()
                                : Array.Empty<string>(),
            CreatedAtUtc   = DateTimeOffset.Parse(node["created_at"].As<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind),
            Metadata       = DeserializeMetadata(node.Properties.TryGetValue("metadata", out var md) ? md.As<string>() : null)
        };

    private static float[]? ReadEmbedding(INode node)
    {
        if (!node.Properties.TryGetValue("embedding", out var ev) || ev is null) return null;
        return ev.As<IList<object>>().Select(v => Convert.ToSingle(v)).ToArray();
    }

    private static string SerializeMetadata(IReadOnlyDictionary<string, object> metadata)
        => metadata.Count == 0 ? "{}" : JsonSerializer.Serialize(metadata);

    private static IReadOnlyDictionary<string, object> DeserializeMetadata(string? json)
        => string.IsNullOrEmpty(json)
            ? new Dictionary<string, object>()
            : JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();
}