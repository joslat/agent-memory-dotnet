using System.Text.Json;
using Microsoft.Extensions.Logging;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Repositories;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;

namespace Neo4j.AgentMemory.Neo4j.Repositories;

public sealed class Neo4jPreferenceRepository : IPreferenceRepository
{
    private readonly INeo4jTransactionRunner _tx;
    private readonly ILogger<Neo4jPreferenceRepository> _logger;

    public Neo4jPreferenceRepository(INeo4jTransactionRunner tx, ILogger<Neo4jPreferenceRepository> logger)
    {
        _tx = tx;
        _logger = logger;
    }

    public async Task<Preference> UpsertAsync(Preference preference, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Upserting preference {Id}", preference.PreferenceId);

        const string cypher = @"
            MERGE (p:Preference {id: $id})
            ON CREATE SET
                p.category           = $category,
                p.preference         = $preferenceText,
                p.context            = $context,
                p.confidence         = $confidence,
                p.source_message_ids = $sourceMessageIds,
                p.created_at         = $createdAtUtc,
                p.metadata           = $metadata
            ON MATCH SET
                p.category           = $category,
                p.preference         = $preferenceText,
                p.context            = $context,
                p.confidence         = $confidence,
                p.source_message_ids = $sourceMessageIds,
                p.metadata           = $metadata
            RETURN p";

        return await _tx.WriteAsync(async runner =>
        {
            var parameters = new Dictionary<string, object?>
            {
                ["id"]               = preference.PreferenceId,
                ["category"]         = preference.Category,
                ["preferenceText"]   = preference.PreferenceText,
                ["context"]          = (object?)preference.Context,
                ["confidence"]       = preference.Confidence,
                ["sourceMessageIds"] = preference.SourceMessageIds.ToList(),
                ["createdAtUtc"]     = preference.CreatedAtUtc.ToString("O"),
                ["metadata"]         = SerializeMetadata(preference.Metadata)
            };

            var cursor = await runner.RunAsync(cypher, parameters);
            var record = await cursor.SingleAsync();
            var node = record["p"].As<INode>();

            if (preference.Embedding is not null)
            {
                await runner.RunAsync(
                    "MATCH (p:Preference {id: $id}) SET p.embedding = $embedding",
                    new { id = preference.PreferenceId, embedding = preference.Embedding.ToList() });
            }

            // Auto-create EXTRACTED_FROM relationships for all source messages
            if (preference.SourceMessageIds.Count > 0)
            {
                await runner.RunAsync(@"
                    MATCH (p:Preference {id: $id})
                    UNWIND $sourceMessageIds AS msgId
                    MATCH (m:Message {id: msgId})
                    MERGE (p)-[:EXTRACTED_FROM]->(m)",
                    new { id = preference.PreferenceId, sourceMessageIds = preference.SourceMessageIds.ToList() });
            }

            return MapToPreference(node, preference.Embedding);
        }, cancellationToken);
    }

    public async Task<Preference?> GetByIdAsync(string preferenceId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting preference {Id}", preferenceId);

        const string cypher = "MATCH (p:Preference {id: $id}) RETURN p";

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, new { id = preferenceId });
            var records = await cursor.ToListAsync();
            if (records.Count == 0) return null;
            var node = records[0]["p"].As<INode>();
            return MapToPreference(node, ReadEmbedding(node));
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<Preference>> GetByCategoryAsync(string category, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting preferences by category '{Category}'", category);

        const string cypher = "MATCH (p:Preference {category: $category}) RETURN p";

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, new { category });
            var records = await cursor.ToListAsync();
            return records.Select(r =>
            {
                var node = r["p"].As<INode>();
                return MapToPreference(node, ReadEmbedding(node));
            }).ToList();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<(Preference Preference, double Score)>> SearchByVectorAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Vector search preferences, limit={Limit}", limit);

        const string cypher = @"
            CALL db.index.vector.queryNodes('preference_embedding_idx', $limit, $embedding)
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
                return (MapToPreference(node, ReadEmbedding(node)), score);
            }).ToList();
        }, cancellationToken);
    }

    public async Task DeleteAsync(string preferenceId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Deleting preference {Id}", preferenceId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                "MATCH (p:Preference {id: $id}) DETACH DELETE p",
                new { id = preferenceId });
        }, cancellationToken);
    }

    public async Task CreateExtractedFromRelationshipAsync(string preferenceId, string messageId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating EXTRACTED_FROM: Preference {PreferenceId} -> Message {MessageId}", preferenceId, messageId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(@"
                MATCH (p:Preference {id: $preferenceId}), (m:Message {id: $messageId})
                MERGE (p)-[:EXTRACTED_FROM]->(m)",
                new { preferenceId, messageId });
        }, cancellationToken);
    }

    public async Task CreateAboutRelationshipAsync(string preferenceId, string entityId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating ABOUT: Preference {PreferenceId} -> Entity {EntityId}", preferenceId, entityId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(@"
                MATCH (p:Preference {id: $preferenceId}), (e:Entity {id: $entityId})
                MERGE (p)-[:ABOUT]->(e)",
                new { preferenceId, entityId });
        }, cancellationToken);
    }

    public async Task CreateConversationPreferenceRelationshipAsync(string conversationId, string preferenceId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating HAS_PREFERENCE: Conversation {ConversationId} -> Preference {PreferenceId}", conversationId, preferenceId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(@"
                MATCH (c:Conversation {id: $conversationId}), (p:Preference {id: $preferenceId})
                MERGE (c)-[:HAS_PREFERENCE]->(p)",
                new { conversationId, preferenceId });
        }, cancellationToken);
    }

    private static Preference MapToPreference(INode node, float[]? embedding) =>
        new()
        {
            PreferenceId     = node["id"].As<string>(),
            Category         = node["category"].As<string>(),
            PreferenceText   = node["preference"].As<string>(),
            Context          = node.Properties.TryGetValue("context", out var ctx) ? ctx.As<string>() : null,
            Confidence       = node["confidence"].As<double>(),
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

    private static string SerializeMetadata(IReadOnlyDictionary<string, object> metadata)
        => metadata.Count == 0 ? "{}" : JsonSerializer.Serialize(metadata);

    private static IReadOnlyDictionary<string, object> DeserializeMetadata(string? json)
        => string.IsNullOrEmpty(json)
            ? new Dictionary<string, object>()
            : JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();
}