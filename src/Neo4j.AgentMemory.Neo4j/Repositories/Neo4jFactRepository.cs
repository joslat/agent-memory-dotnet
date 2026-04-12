using System.Text.Json;
using Microsoft.Extensions.Logging;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Repositories;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;

namespace Neo4j.AgentMemory.Neo4j.Repositories;

public sealed class Neo4jFactRepository : IFactRepository
{
    private readonly INeo4jTransactionRunner _tx;
    private readonly ILogger<Neo4jFactRepository> _logger;

    public Neo4jFactRepository(INeo4jTransactionRunner tx, ILogger<Neo4jFactRepository> logger)
    {
        _tx = tx;
        _logger = logger;
    }

    public async Task<Fact> UpsertAsync(Fact fact, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Upserting fact {Id}", fact.FactId);

        const string cypher = @"
            MERGE (f:Fact {id: $id})
            ON CREATE SET
                f.subject          = $subject,
                f.predicate        = $predicate,
                f.object           = $object,
                f.confidence       = $confidence,
                f.validFrom        = $validFrom,
                f.validUntil       = $validUntil,
                f.sourceMessageIds = $sourceMessageIds,
                f.createdAtUtc     = $createdAtUtc,
                f.metadata         = $metadata
            ON MATCH SET
                f.subject          = $subject,
                f.predicate        = $predicate,
                f.object           = $object,
                f.confidence       = $confidence,
                f.validFrom        = $validFrom,
                f.validUntil       = $validUntil,
                f.sourceMessageIds = $sourceMessageIds,
                f.metadata         = $metadata
            RETURN f";

        return await _tx.WriteAsync(async runner =>
        {
            var parameters = new Dictionary<string, object?>
            {
                ["id"]               = fact.FactId,
                ["subject"]          = fact.Subject,
                ["predicate"]        = fact.Predicate,
                ["object"]           = fact.Object,
                ["confidence"]       = fact.Confidence,
                ["validFrom"]        = (object?)(fact.ValidFrom?.ToString("O")),
                ["validUntil"]       = (object?)(fact.ValidUntil?.ToString("O")),
                ["sourceMessageIds"] = fact.SourceMessageIds.ToList(),
                ["createdAtUtc"]     = fact.CreatedAtUtc.ToString("O"),
                ["metadata"]         = SerializeMetadata(fact.Metadata)
            };

            var cursor = await runner.RunAsync(cypher, parameters);
            var record = await cursor.SingleAsync();
            var node = record["f"].As<INode>();

            if (fact.Embedding is not null)
            {
                await runner.RunAsync(
                    "MATCH (f:Fact {id: $id}) SET f.embedding = $embedding",
                    new { id = fact.FactId, embedding = fact.Embedding.ToList() });
            }

            return MapToFact(node, fact.Embedding);
        }, cancellationToken);
    }

    public async Task<Fact?> GetByIdAsync(string factId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting fact {Id}", factId);

        const string cypher = "MATCH (f:Fact {id: $id}) RETURN f";

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, new { id = factId });
            var records = await cursor.ToListAsync();
            if (records.Count == 0) return null;
            var node = records[0]["f"].As<INode>();
            return MapToFact(node, ReadEmbedding(node));
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<Fact>> GetBySubjectAsync(string subject, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting facts by subject '{Subject}'", subject);

        const string cypher = "MATCH (f:Fact {subject: $subject}) RETURN f";

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, new { subject });
            var records = await cursor.ToListAsync();
            return records.Select(r =>
            {
                var node = r["f"].As<INode>();
                return MapToFact(node, ReadEmbedding(node));
            }).ToList();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<(Fact Fact, double Score)>> SearchByVectorAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Vector search facts, limit={Limit}", limit);

        const string cypher = @"
            CALL db.index.vector.queryNodes('fact_embedding_idx', $limit, $embedding)
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
                return (MapToFact(node, ReadEmbedding(node)), score);
            }).ToList();
        }, cancellationToken);
    }

    private static Fact MapToFact(INode node, float[]? embedding) =>
        new()
        {
            FactId           = node["id"].As<string>(),
            Subject          = node["subject"].As<string>(),
            Predicate        = node["predicate"].As<string>(),
            Object           = node["object"].As<string>(),
            Confidence       = node["confidence"].As<double>(),
            ValidFrom        = node.Properties.TryGetValue("validFrom", out var vf) && vf.As<string>() is { } vfStr && !string.IsNullOrEmpty(vfStr)
                                ? DateTimeOffset.Parse(vfStr, null, System.Globalization.DateTimeStyles.RoundtripKind)
                                : null,
            ValidUntil       = node.Properties.TryGetValue("validUntil", out var vu) && vu.As<string>() is { } vuStr && !string.IsNullOrEmpty(vuStr)
                                ? DateTimeOffset.Parse(vuStr, null, System.Globalization.DateTimeStyles.RoundtripKind)
                                : null,
            Embedding        = embedding,
            SourceMessageIds = node.Properties.TryGetValue("sourceMessageIds", out var sm)
                                ? sm.As<IList<object>>().Select(v => v.ToString()!).ToList()
                                : Array.Empty<string>(),
            CreatedAtUtc     = DateTimeOffset.Parse(node["createdAtUtc"].As<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind),
            Metadata         = DeserializeMetadata(node.Properties.TryGetValue("metadata", out var md) ? md.As<string>() : null)
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
