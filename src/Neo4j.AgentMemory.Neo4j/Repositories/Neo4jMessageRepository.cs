using System.Text.Json;
using Microsoft.Extensions.Logging;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Repositories;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;

namespace Neo4j.AgentMemory.Neo4j.Repositories;

public sealed class Neo4jMessageRepository : IMessageRepository
{
    private readonly INeo4jTransactionRunner _tx;
    private readonly ILogger<Neo4jMessageRepository> _logger;

    public Neo4jMessageRepository(INeo4jTransactionRunner tx, ILogger<Neo4jMessageRepository> logger)
    {
        _tx = tx;
        _logger = logger;
    }

    public async Task<Message> AddAsync(Message message, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Adding message {Id} to conversation {ConvId}", message.MessageId, message.ConversationId);

        const string createCypher = @"
            MATCH (conv:Conversation {id: $conversationId})
            CREATE (m:Message {
                id:              $id,
                conversation_id: $conversationId,
                session_id:      $sessionId,
                role:            $role,
                content:         $content,
                timestamp:       $timestamp,
                tool_call_ids:   $toolCallIds,
                metadata:        $metadata
            })
            CREATE (conv)-[:HAS_MESSAGE]->(m)
            RETURN m";

        const string linkedListCypher = @"
            MATCH (conv:Conversation {id: $conversationId})-[:HAS_MESSAGE]->(prev:Message)
            WHERE prev.id <> $id
            WITH prev ORDER BY prev.timestamp DESC LIMIT 1
            MATCH (m:Message {id: $id})
            CREATE (prev)-[:NEXT_MESSAGE]->(m)";

        var parameters = BuildMessageParameters(message);

        return await _tx.WriteAsync(async runner =>
        {
            // Store embedding separately to handle null
            var createParams = new Dictionary<string, object?>
            {
                ["id"]             = message.MessageId,
                ["conversationId"] = message.ConversationId,
                ["sessionId"]      = message.SessionId,
                ["role"]           = message.Role,
                ["content"]        = message.Content,
                ["timestamp"]      = message.TimestampUtc.ToString("O"),
                ["toolCallIds"]    = message.ToolCallIds?.ToList() ?? new List<string>(),
                ["metadata"]       = SerializeMetadata(message.Metadata)
            };

            var cursor = await runner.RunAsync(createCypher, createParams);
            var record = await cursor.SingleAsync();
            var node = record["m"].As<INode>();

            if (message.Embedding is not null)
            {
                await runner.RunAsync(
                    "MATCH (m:Message {id: $id}) SET m.embedding = $embedding",
                    new { id = message.MessageId, embedding = message.Embedding.ToList() });
            }

            // Create FIRST_MESSAGE if this is the first message in the conversation
            await runner.RunAsync(@"
                MATCH (conv:Conversation {id: $conversationId}), (m:Message {id: $id})
                WHERE NOT EXISTS { MATCH (conv)-[:FIRST_MESSAGE]->() }
                MERGE (conv)-[:FIRST_MESSAGE]->(m)",
                new { conversationId = message.ConversationId, id = message.MessageId });

            // Establish NEXT_MESSAGE link from the previous last message
            await runner.RunAsync(linkedListCypher, new { conversationId = message.ConversationId, id = message.MessageId });

            return MapToMessage(node, message.Embedding);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<Message>> AddBatchAsync(IEnumerable<Message> messages, CancellationToken cancellationToken = default)
    {
        var ordered = messages.OrderBy(m => m.TimestampUtc).ToList();
        if (ordered.Count == 0) return Array.Empty<Message>();

        _logger.LogDebug("Batch adding {Count} messages", ordered.Count);

        const string createCypher = @"
            UNWIND $messages AS msg
            MATCH (conv:Conversation {id: msg.conversation_id})
            CREATE (m:Message {
                id:              msg.id,
                conversation_id: msg.conversation_id,
                session_id:      msg.session_id,
                role:            msg.role,
                content:         msg.content,
                timestamp:       msg.timestamp,
                tool_call_ids:   msg.tool_call_ids,
                metadata:        msg.metadata
            })
            CREATE (conv)-[:HAS_MESSAGE]->(m)
            RETURN m";

        var msgParams = ordered.Select(m => new Dictionary<string, object?>
        {
            ["id"]              = m.MessageId,
            ["conversation_id"] = m.ConversationId,
            ["session_id"]      = m.SessionId,
            ["role"]            = m.Role,
            ["content"]         = m.Content,
            ["timestamp"]       = m.TimestampUtc.ToString("O"),
            ["tool_call_ids"]   = m.ToolCallIds?.ToList() ?? new List<string>(),
            ["metadata"]        = SerializeMetadata(m.Metadata)
        }).ToList();

        return await _tx.WriteAsync(async runner =>
        {
            var cursor = await runner.RunAsync(createCypher, new { messages = msgParams });
            // consume result
            await cursor.ConsumeAsync();

            // Set embeddings
            foreach (var msg in ordered.Where(m => m.Embedding is not null))
            {
                await runner.RunAsync(
                    "MATCH (m:Message {id: $id}) SET m.embedding = $embedding",
                    new { id = msg.MessageId, embedding = msg.Embedding!.ToList() });
            }

            // Create NEXT_MESSAGE chain within batch
            for (int i = 1; i < ordered.Count; i++)
            {
                await runner.RunAsync(
                    "MATCH (prev:Message {id: $prevId}), (next:Message {id: $nextId}) CREATE (prev)-[:NEXT_MESSAGE]->(next)",
                    new { prevId = ordered[i - 1].MessageId, nextId = ordered[i].MessageId });
            }

            // Connect first batch message to any existing last message in the conversation
            if (ordered.Count > 0)
            {
                await runner.RunAsync(@"
                    MATCH (conv:Conversation {id: $conversationId})-[:HAS_MESSAGE]->(prev:Message)
                    WHERE NOT prev.id IN $batchIds
                    WITH prev ORDER BY prev.timestamp DESC LIMIT 1
                    MATCH (first:Message {id: $firstId})
                    CREATE (prev)-[:NEXT_MESSAGE]->(first)",
                    new
                    {
                        conversationId = ordered[0].ConversationId,
                        batchIds       = ordered.Select(m => m.MessageId).ToList(),
                        firstId        = ordered[0].MessageId
                    });
            }

            // Re-read all created messages
            var readCursor = await runner.RunAsync(
                "MATCH (m:Message) WHERE m.id IN $ids RETURN m ORDER BY m.timestamp",
                new { ids = ordered.Select(m => m.MessageId).ToList() });
            var records = await readCursor.ToListAsync();

            var embeddingMap = ordered.ToDictionary(m => m.MessageId, m => m.Embedding);
            return records.Select(r =>
            {
                var node = r["m"].As<INode>();
                var id = node["id"].As<string>();
                return MapToMessage(node, embeddingMap.TryGetValue(id, out var emb) ? emb : null);
            }).ToList();
        }, cancellationToken);
    }

    public async Task<Message?> GetByIdAsync(string messageId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting message {Id}", messageId);

        const string cypher = "MATCH (m:Message {id: $id}) RETURN m";

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, new { id = messageId });
            var records = await cursor.ToListAsync();
            if (records.Count == 0) return null;
            var node = records[0]["m"].As<INode>();
            return MapToMessage(node, ReadEmbedding(node));
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<Message>> GetByConversationAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting messages for conversation {Id}", conversationId);

        const string cypher = @"
            MATCH (c:Conversation {id: $conversationId})-[:HAS_MESSAGE]->(m:Message)
            RETURN m
            ORDER BY m.timestamp";

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, new { conversationId });
            var records = await cursor.ToListAsync();
            return records.Select(r =>
            {
                var node = r["m"].As<INode>();
                return MapToMessage(node, ReadEmbedding(node));
            }).ToList();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<Message>> GetRecentBySessionAsync(string sessionId, int limit, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting {Limit} recent messages for session {SessionId}", limit, sessionId);

        const string cypher = @"
            MATCH (m:Message {session_id: $sessionId})
            RETURN m
            ORDER BY m.timestamp DESC
            LIMIT $limit";

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, new { sessionId, limit });
            var records = await cursor.ToListAsync();
            return records.Select(r =>
            {
                var node = r["m"].As<INode>();
                return MapToMessage(node, ReadEmbedding(node));
            }).ToList();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<(Message Message, double Score)>> SearchByVectorAsync(
        float[] queryEmbedding,
        string? sessionId = null,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Vector search messages, sessionId={SessionId}, limit={Limit}", sessionId, limit);

        var cypher = sessionId is null
            ? @"
                CALL db.index.vector.queryNodes('message_embedding_idx', $limit, $embedding)
                YIELD node, score
                WHERE score >= $minScore
                RETURN node, score
                ORDER BY score DESC"
            : @"
                CALL db.index.vector.queryNodes('message_embedding_idx', $limit, $embedding)
                YIELD node, score
                WHERE score >= $minScore AND node.session_id = $sessionId
                RETURN node, score
                ORDER BY score DESC";

        var parameters = new Dictionary<string, object>
        {
            ["embedding"] = queryEmbedding.ToList(),
            ["limit"]     = limit,
            ["minScore"]  = minScore
        };
        if (sessionId is not null) parameters["sessionId"] = sessionId;

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, parameters);
            var records = await cursor.ToListAsync();
            return records.Select(r =>
            {
                var node  = r["node"].As<INode>();
                var score = r["score"].As<double>();
                return (MapToMessage(node, ReadEmbedding(node)), score);
            }).ToList();
        }, cancellationToken);
    }

    public async Task DeleteBySessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Deleting messages for session {SessionId}", sessionId);

        const string cypher = "MATCH (m:Message {session_id: $sessionId}) DETACH DELETE m";

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(cypher, new { sessionId });
        }, cancellationToken);
    }

    private static Message MapToMessage(INode node, float[]? embedding) =>
        new()
        {
            MessageId      = node["id"].As<string>(),
            ConversationId = node["conversation_id"].As<string>(),
            SessionId      = node["session_id"].As<string>(),
            Role           = node["role"].As<string>(),
            Content        = node["content"].As<string>(),
            TimestampUtc   = DateTimeOffset.Parse(node["timestamp"].As<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind),
            Embedding      = embedding,
            ToolCallIds    = node.Properties.TryGetValue("tool_call_ids", out var tc)
                                ? tc.As<IList<object>>().Select(v => v.ToString()!).ToList()
                                : null,
            Metadata       = DeserializeMetadata(node.Properties.TryGetValue("metadata", out var md) ? md.As<string>() : null)
        };

    private static float[]? ReadEmbedding(INode node)
    {
        if (!node.Properties.TryGetValue("embedding", out var ev) || ev is null) return null;
        return ev.As<IList<object>>().Select(v => Convert.ToSingle(v)).ToArray();
    }

    private static Dictionary<string, object?> BuildMessageParameters(Message m) => new()
    {
        ["id"]             = m.MessageId,
        ["conversationId"] = m.ConversationId,
        ["sessionId"]      = m.SessionId,
        ["role"]           = m.Role,
        ["content"]        = m.Content,
        ["timestamp"]      = m.TimestampUtc.ToString("O"),
        ["toolCallIds"]    = m.ToolCallIds?.ToList() ?? new List<string>(),
        ["metadata"]       = SerializeMetadata(m.Metadata)
    };

    private static string SerializeMetadata(IReadOnlyDictionary<string, object> metadata)
        => metadata.Count == 0 ? "{}" : JsonSerializer.Serialize(metadata);

    private static IReadOnlyDictionary<string, object> DeserializeMetadata(string? json)
        => string.IsNullOrEmpty(json)
            ? new Dictionary<string, object>()
            : JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();
}
