using System.ComponentModel;
using ModelContextProtocol.Server;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.McpServer.Tools;

namespace Neo4j.AgentMemory.McpServer.Resources;

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
        CancellationToken cancellationToken = default)
    {
        var query = """
            MATCH (c:Conversation)
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

        var results = await graphQueryService.QueryAsync(query, parameters, cancellationToken);

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
