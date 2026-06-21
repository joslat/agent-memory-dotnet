using System.ComponentModel;
using ModelContextProtocol.Server;
using AgentMemory.Abstractions.Services;
using AgentMemory.McpServer.Tools;

namespace AgentMemory.McpServer.Resources;

/// <summary>
/// MCP resource that lists recent conversations.
/// </summary>
[McpServerResourceType]
public sealed class ConversationListResource
{
    [McpServerResource(UriTemplate = "memory://conversations", Name = "memory_conversations", MimeType = "application/json"),
     Description("Returns recent conversations with message counts.")]
    public static async Task<string> GetConversations(
        IGraphQueryService graphQueryService,
        [Description("Maximum number of conversations to return")] int limit = 20,
        [Description("Owner/user identifier (optional). When set, returns only that user's (plus un-attributed) conversations; null = all users (unscoped/admin). Set it in multi-tenant deployments to avoid leaking other users' session ids (R1).")] string? userId = null,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 1000); // guard against negative (Neo4j error) / huge (resource-exhaustion) limits

        // Conversations carry user_id (not owner_id). Scope to the user's own + un-attributed rows.
        var hasOwner = !string.IsNullOrEmpty(userId);
        var whereClause = hasOwner ? "WHERE (c.user_id = $userId OR c.user_id IS NULL)" : "";

        var query = $"""
            MATCH (c:Conversation)
            {whereClause}
            OPTIONAL MATCH (c)-[:HAS_MESSAGE]->(m:Message)
            WITH c, count(m) AS messageCount
            ORDER BY c.created_at DESC
            LIMIT $limit
            RETURN c.id AS id, c.session_id AS sessionId,
                   c.created_at AS createdAt, messageCount
            """;

        var parameters = new Dictionary<string, object?>
        {
            ["limit"] = (long)limit
        };
        if (hasOwner) parameters["userId"] = userId;

        var results = await graphQueryService.QueryAsync(query, parameters, cancellationToken).ConfigureAwait(false);

        return ToolJsonContext.Serialize(new
        {
            conversations = results.Select(r => new
            {
                id = r.TryGetValue("id", out var id) ? id?.ToString() : null,
                sessionId = r.TryGetValue("sessionId", out var sid) ? sid?.ToString() : null,
                createdAt = r.TryGetValue("createdAt", out var ca) ? ca?.ToString() : null,
                messageCount = r.TryGetValue("messageCount", out var mc) ? Convert.ToInt64(mc) : 0L
            }),
            limit
        });
    }
}
