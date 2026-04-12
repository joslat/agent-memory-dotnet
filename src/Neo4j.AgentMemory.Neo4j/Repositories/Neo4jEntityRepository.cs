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
                e.name             = $name,
                e.canonicalName    = $canonicalName,
                e.type             = $type,
                e.subtype          = $subtype,
                e.description      = $description,
                e.confidence       = $confidence,
                e.aliases          = $aliases,
                e.attributes       = $attributes,
                e.sourceMessageIds = $sourceMessageIds,
                e.createdAtUtc     = $createdAtUtc,
                e.metadata         = $metadata
            ON MATCH SET
                e.name             = $name,
                e.canonicalName    = $canonicalName,
                e.type             = $type,
                e.subtype          = $subtype,
                e.description      = $description,
                e.confidence       = $confidence,
                e.aliases          = $aliases,
                e.attributes       = $attributes,
                e.sourceMessageIds = $sourceMessageIds,
                e.metadata         = $metadata
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

    private static Entity MapToEntity(INode node, float[]? embedding) =>
        new()
        {
            EntityId       = node["id"].As<string>(),
            Name           = node["name"].As<string>(),
            CanonicalName  = node.Properties.TryGetValue("canonicalName", out var cn) ? cn.As<string>() : null,
            Type           = node["type"].As<string>(),
            Subtype        = node.Properties.TryGetValue("subtype", out var st) ? st.As<string>() : null,
            Description    = node.Properties.TryGetValue("description", out var desc) ? desc.As<string>() : null,
            Confidence     = node["confidence"].As<double>(),
            Embedding      = embedding,
            Aliases        = node.Properties.TryGetValue("aliases", out var al)
                                ? al.As<IList<object>>().Select(a => a.ToString()!).ToList()
                                : Array.Empty<string>(),
            Attributes     = DeserializeMetadata(node.Properties.TryGetValue("attributes", out var attr) ? attr.As<string>() : null),
            SourceMessageIds = node.Properties.TryGetValue("sourceMessageIds", out var sm)
                                ? sm.As<IList<object>>().Select(v => v.ToString()!).ToList()
                                : Array.Empty<string>(),
            CreatedAtUtc   = DateTimeOffset.Parse(node["createdAtUtc"].As<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind),
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
