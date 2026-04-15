using System.Text.Json;
using Microsoft.Extensions.Logging;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Repositories;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;

namespace Neo4j.AgentMemory.Neo4j.Repositories;

public sealed class Neo4jConversationRepository : IConversationRepository
{
    private readonly INeo4jTransactionRunner _tx;
    private readonly ILogger<Neo4jConversationRepository> _logger;

    public Neo4jConversationRepository(INeo4jTransactionRunner tx, ILogger<Neo4jConversationRepository> logger)
    {
        _tx = tx;
        _logger = logger;
    }

    public async Task<Conversation> UpsertAsync(Conversation conversation, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Upserting conversation {Id}", conversation.ConversationId);

        const string cypher = @"
            MERGE (c:Conversation {id: $id})
            ON CREATE SET
                c.session_id  = $sessionId,
                c.user_id     = $userId,
                c.title       = $title,
                c.created_at  = datetime($createdAtUtc),
                c.updated_at  = datetime($updatedAtUtc),
                c.metadata    = $metadata
            ON MATCH SET
                c.session_id  = $sessionId,
                c.user_id     = $userId,
                c.title       = $title,
                c.updated_at  = datetime($updatedAtUtc),
                c.metadata    = $metadata
            RETURN c";

        var parameters = new
        {
            id           = conversation.ConversationId,
            sessionId    = conversation.SessionId,
            userId       = (object?)conversation.UserId,
            title        = (object?)conversation.Title,
            createdAtUtc = conversation.CreatedAtUtc.ToString("O"),
            updatedAtUtc = conversation.UpdatedAtUtc.ToString("O"),
            metadata     = SerializeMetadata(conversation.Metadata)
        };

        return await _tx.WriteAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, parameters);
            var record = await cursor.SingleAsync();
            return MapToConversation(record["c"].As<INode>());
        }, cancellationToken);
    }

    public async Task<Conversation?> GetByIdAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting conversation {Id}", conversationId);

        const string cypher = "MATCH (c:Conversation {id: $id}) RETURN c";

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, new { id = conversationId });
            var records = await cursor.ToListAsync();
            return records.Count == 0 ? null : MapToConversation(records[0]["c"].As<INode>());
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<Conversation>> GetBySessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting conversations for session {SessionId}", sessionId);

        const string cypher = @"
            MATCH (c:Conversation {session_id: $sessionId})
            RETURN c
            ORDER BY c.updated_at DESC";

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, new { sessionId });
            var records = await cursor.ToListAsync();
            return records.Select(r => MapToConversation(r["c"].As<INode>())).ToList();
        }, cancellationToken);
    }

    public async Task DeleteAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Deleting conversation {Id}", conversationId);

        const string cypher = "MATCH (c:Conversation {id: $id}) DETACH DELETE c";

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(cypher, new { id = conversationId });
        }, cancellationToken);
    }

    private static Conversation MapToConversation(INode node) =>
        new()
        {
            ConversationId = node["id"].As<string>(),
            SessionId      = node["session_id"].As<string>(),
            UserId         = node.Properties.TryGetValue("user_id", out var uid) ? uid.As<string>() : null,
            Title          = node.Properties.TryGetValue("title", out var t) && t is not null ? t.As<string>() : null,
            CreatedAtUtc   = Neo4jDateTimeHelper.ReadDateTimeOffset(node["created_at"]),
            UpdatedAtUtc   = Neo4jDateTimeHelper.ReadDateTimeOffset(node["updated_at"]),
            Metadata       = DeserializeMetadata(node.Properties.TryGetValue("metadata", out var md) ? md.As<string>() : null)
        };

    private static string SerializeMetadata(IReadOnlyDictionary<string, object> metadata)
        => metadata.Count == 0 ? "{}" : JsonSerializer.Serialize(metadata);

    private static IReadOnlyDictionary<string, object> DeserializeMetadata(string? json)
        => string.IsNullOrEmpty(json)
            ? new Dictionary<string, object>()
            : JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();
}
