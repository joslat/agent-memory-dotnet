using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using Neo4j.Driver;
using static AgentMemory.Neo4j.Repositories.Neo4jRecordMapper;

namespace AgentMemory.Neo4j.Repositories;

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
            var cursor = await runner.RunAsync(ConversationQueries.Upsert, parameters).ConfigureAwait(false);
            var record = await cursor.SingleAsync().ConfigureAwait(false);
            return MapToConversation(record["c"].As<INode>());
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Conversation?> GetByIdAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting conversation {Id}", conversationId);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(ConversationQueries.GetById, new { id = conversationId }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Count == 0 ? null : MapToConversation(records[0]["c"].As<INode>());
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Conversation>> GetBySessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting conversations for session {SessionId}", sessionId);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(ConversationQueries.GetBySession, new { sessionId }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Select(r => MapToConversation(r["c"].As<INode>())).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Deleting conversation {Id}", conversationId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(ConversationQueries.Delete, new { id = conversationId }).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteBySessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Deleting all conversations for session {SessionId}", sessionId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(ConversationQueries.DeleteBySession, new { sessionId }).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(int limit = 50, CancellationToken ct = default)
    {
        _logger.LogDebug("Listing sessions, limit={Limit}", limit);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(ConversationQueries.ListSessions, new { limit }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Select(r =>
            {
                var sessionId = r["sessionId"].As<string>();
                var convCount = r["convCount"].As<int>();
                var msgCount = r["msgCount"].As<int>();
                var lastPreview = r["lastPreview"] is not null ? r["lastPreview"].As<string>() : null;
                DateTimeOffset? lastActivity = r["lastActivity"] is not null
                    ? Neo4jDateTimeHelper.ReadDateTimeOffset(r["lastActivity"])
                    : null;
                return new SessionSummary(sessionId, convCount, msgCount, lastPreview, lastActivity);
            }).ToList();
        }, ct).ConfigureAwait(false);
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
            Archived       = node.Properties.TryGetValue("archived", out var arch) && arch is not null && arch.As<bool>(),
            Metadata       = DeserializeMetadata(node.Properties.TryGetValue("metadata", out var md) ? md.As<string>() : null)
        };
}
