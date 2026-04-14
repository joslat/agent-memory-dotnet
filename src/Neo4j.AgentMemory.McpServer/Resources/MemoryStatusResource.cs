using System.ComponentModel;
using ModelContextProtocol.Server;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.McpServer.Tools;

namespace Neo4j.AgentMemory.McpServer.Resources;

/// <summary>
/// MCP resource that exposes memory status and statistics.
/// </summary>
[McpServerResourceType]
public sealed class MemoryStatusResource
{
    [McpServerResource(UriTemplate = "memory://status", Name = "memory_status", MimeType = "application/json"),
     Description("Returns current memory statistics: entity count, fact count, preference count, conversation count, message count.")]
    public static async Task<string> GetMemoryStatus(
        IGraphQueryService graphQueryService,
        CancellationToken cancellationToken = default)
    {
        var countQuery = """
            OPTIONAL MATCH (e:Entity)
            WITH count(e) AS entityCount
            OPTIONAL MATCH (f:Fact)
            WITH entityCount, count(f) AS factCount
            OPTIONAL MATCH (p:Preference)
            WITH entityCount, factCount, count(p) AS preferenceCount
            OPTIONAL MATCH (c:Conversation)
            WITH entityCount, factCount, preferenceCount, count(c) AS conversationCount
            OPTIONAL MATCH (m:Message)
            RETURN entityCount, factCount, preferenceCount, conversationCount, count(m) AS messageCount
            """;

        var results = await graphQueryService.QueryAsync(countQuery, cancellationToken: cancellationToken);
        var row = results.FirstOrDefault();

        return ToolJsonContext.Serialize(new
        {
            entityCount = row != null && row.TryGetValue("entityCount", out var ec) ? Convert.ToInt64(ec) : 0L,
            factCount = row != null && row.TryGetValue("factCount", out var fc) ? Convert.ToInt64(fc) : 0L,
            preferenceCount = row != null && row.TryGetValue("preferenceCount", out var pc) ? Convert.ToInt64(pc) : 0L,
            conversationCount = row != null && row.TryGetValue("conversationCount", out var cc) ? Convert.ToInt64(cc) : 0L,
            messageCount = row != null && row.TryGetValue("messageCount", out var mc) ? Convert.ToInt64(mc) : 0L,
            retrievedAtUtc = DateTimeOffset.UtcNow
        });
    }
}
