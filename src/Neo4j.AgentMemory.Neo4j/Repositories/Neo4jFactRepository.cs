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
                f.subject            = $subject,
                f.predicate          = $predicate,
                f.object             = $object,
                f.confidence         = $confidence,
                f.valid_from         = $validFrom,
                f.valid_until        = $validUntil,
                f.source_message_ids = $sourceMessageIds,
                f.created_at         = $createdAtUtc,
                f.metadata           = $metadata
            ON MATCH SET
                f.subject            = $subject,
                f.predicate          = $predicate,
                f.object             = $object,
                f.confidence         = $confidence,
                f.valid_from         = $validFrom,
                f.valid_until        = $validUntil,
                f.source_message_ids = $sourceMessageIds,
                f.metadata           = $metadata
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

            // Auto-create EXTRACTED_FROM relationships for all source messages
            if (fact.SourceMessageIds.Count > 0)
            {
                await runner.RunAsync(@"
                    MATCH (f:Fact {id: $id})
                    UNWIND $sourceMessageIds AS msgId
                    MATCH (m:Message {id: msgId})
                    MERGE (f)-[:EXTRACTED_FROM]->(m)",
                    new { id = fact.FactId, sourceMessageIds = fact.SourceMessageIds.ToList() });
            }

            return MapToFact(node, fact.Embedding);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<Fact>> UpsertBatchAsync(IReadOnlyList<Fact> facts, CancellationToken cancellationToken = default)
    {
        if (facts.Count == 0) return Array.Empty<Fact>();

        _logger.LogDebug("Batch upserting {Count} facts", facts.Count);

        const string mergeCypher = @"
            UNWIND $items AS item
            MERGE (f:Fact {id: item.id})
            ON CREATE SET
                f.subject            = item.subject,
                f.predicate          = item.predicate,
                f.object             = item.object,
                f.confidence         = item.confidence,
                f.valid_from         = item.valid_from,
                f.valid_until        = item.valid_until,
                f.source_message_ids = item.source_message_ids,
                f.created_at         = item.created_at,
                f.metadata           = item.metadata
            ON MATCH SET
                f.subject            = item.subject,
                f.predicate          = item.predicate,
                f.object             = item.object,
                f.confidence         = item.confidence,
                f.valid_from         = item.valid_from,
                f.valid_until        = item.valid_until,
                f.source_message_ids = item.source_message_ids,
                f.metadata           = item.metadata
            RETURN f";

        var items = facts.Select(f => new Dictionary<string, object?>
        {
            ["id"]                = f.FactId,
            ["subject"]           = f.Subject,
            ["predicate"]         = f.Predicate,
            ["object"]            = f.Object,
            ["confidence"]        = f.Confidence,
            ["valid_from"]        = (object?)(f.ValidFrom?.ToString("O")),
            ["valid_until"]       = (object?)(f.ValidUntil?.ToString("O")),
            ["source_message_ids"] = f.SourceMessageIds.ToList(),
            ["created_at"]        = f.CreatedAtUtc.ToString("O"),
            ["metadata"]          = SerializeMetadata(f.Metadata)
        }).ToList();

        return await _tx.WriteAsync(async runner =>
        {
            var cursor = await runner.RunAsync(mergeCypher, new { items });
            var records = await cursor.ToListAsync();

            // Set embeddings individually
            foreach (var fact in facts.Where(f => f.Embedding is not null))
            {
                await runner.RunAsync(
                    "MATCH (f:Fact {id: $id}) SET f.embedding = $embedding",
                    new { id = fact.FactId, embedding = fact.Embedding!.ToList() });
            }

            // Auto-create EXTRACTED_FROM relationships
            var factsWithSources = facts.Where(f => f.SourceMessageIds.Count > 0).ToList();
            if (factsWithSources.Count > 0)
            {
                foreach (var fact in factsWithSources)
                {
                    await runner.RunAsync(@"
                        MATCH (f:Fact {id: $id})
                        UNWIND $sourceMessageIds AS msgId
                        MATCH (m:Message {id: msgId})
                        MERGE (f)-[:EXTRACTED_FROM]->(m)",
                        new { id = fact.FactId, sourceMessageIds = fact.SourceMessageIds.ToList() });
                }
            }

            var embeddingMap = facts.ToDictionary(f => f.FactId, f => f.Embedding);
            return records.Select(r =>
            {
                var node = r["f"].As<INode>();
                var id   = node["id"].As<string>();
                return MapToFact(node, embeddingMap.TryGetValue(id, out var emb) ? emb : null);
            }).ToList();
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

    public async Task CreateExtractedFromRelationshipAsync(string factId, string messageId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating EXTRACTED_FROM: Fact {FactId} -> Message {MessageId}", factId, messageId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(@"
                MATCH (f:Fact {id: $factId}), (m:Message {id: $messageId})
                MERGE (f)-[:EXTRACTED_FROM]->(m)",
                new { factId, messageId });
        }, cancellationToken);
    }

    public async Task CreateAboutRelationshipAsync(string factId, string entityId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating ABOUT: Fact {FactId} -> Entity {EntityId}", factId, entityId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(@"
                MATCH (f:Fact {id: $factId}), (e:Entity {id: $entityId})
                MERGE (f)-[:ABOUT]->(e)",
                new { factId, entityId });
        }, cancellationToken);
    }

    public async Task CreateConversationFactRelationshipAsync(string conversationId, string factId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating HAS_FACT: Conversation {ConversationId} -> Fact {FactId}", conversationId, factId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(@"
                MATCH (c:Conversation {id: $conversationId}), (f:Fact {id: $factId})
                MERGE (c)-[:HAS_FACT]->(f)",
                new { conversationId, factId });
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
            ValidFrom        = node.Properties.TryGetValue("valid_from", out var vf) && vf.As<string>() is { } vfStr && !string.IsNullOrEmpty(vfStr)
                                ? DateTimeOffset.Parse(vfStr, null, System.Globalization.DateTimeStyles.RoundtripKind)
                                : null,
            ValidUntil       = node.Properties.TryGetValue("valid_until", out var vu) && vu.As<string>() is { } vuStr && !string.IsNullOrEmpty(vuStr)
                                ? DateTimeOffset.Parse(vuStr, null, System.Globalization.DateTimeStyles.RoundtripKind)
                                : null,
            Embedding        = embedding,
            SourceMessageIds = node.Properties.TryGetValue("source_message_ids", out var sm)
                                ? sm.As<IList<object>>().Select(v => v.ToString()!).ToList()
                                : Array.Empty<string>(),
            CreatedAtUtc     = DateTimeOffset.Parse(node["created_at"].As<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind),
            Metadata         = DeserializeMetadata(node.Properties.TryGetValue("metadata", out var md) ? md.As<string>() : null)
        };

    private static float[]? ReadEmbedding(INode node)
    {
        if (!node.Properties.TryGetValue("embedding", out var ev) || ev is null) return null;
        return ev.As<IList<object>>().Select(v => Convert.ToSingle(v)).ToArray();
    }

    public async Task<IReadOnlyList<Fact>> GetPageWithoutEmbeddingAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting up to {Limit} facts without embeddings", limit);

        const string cypher = "MATCH (f:Fact) WHERE f.embedding IS NULL RETURN f LIMIT $limit";

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, new { limit });
            var records = await cursor.ToListAsync();
            return records.Select(r =>
            {
                var node = r["f"].As<INode>();
                return MapToFact(node, null);
            }).ToList();
        }, cancellationToken);
    }

    public async Task UpdateEmbeddingAsync(
        string factId,
        float[] embedding,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Updating embedding for fact {Id}", factId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                "MATCH (f:Fact {id: $id}) SET f.embedding = $embedding",
                new { id = factId, embedding = embedding.ToList() });
        }, cancellationToken);
    }

    private static string SerializeMetadata(IReadOnlyDictionary<string, object> metadata)
        => metadata.Count == 0 ? "{}" : JsonSerializer.Serialize(metadata);

    private static IReadOnlyDictionary<string, object> DeserializeMetadata(string? json)
        => string.IsNullOrEmpty(json)
            ? new Dictionary<string, object>()
            : JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();
}