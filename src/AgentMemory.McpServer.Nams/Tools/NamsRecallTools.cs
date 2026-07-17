using System.ComponentModel;
using ModelContextProtocol.Server;
using AgentMemory.Nams.Recall;

namespace AgentMemory.McpServer.Nams.Tools;

/// <summary>
/// Read-only NAMS recall tool. Registered by <c>AddNamsAgentMemoryMcpTools</c> -- MCP is a secondary
/// interoperability surface (ADR-8): the automatic pre-turn recall <c>NamsMemoryContextProvider</c> performs
/// does not depend on this tool existing, being registered, or ever being called.
/// </summary>
[McpServerToolType]
internal sealed class NamsRecallTools
{
    [McpServerTool(Name = "nams_recall"),
     Description("Recall NAMS-hosted memory (reflections, observations, recent messages, and entities) for " +
                  "a specific, already-resolved NAMS conversation.")]
    public static async Task<string> NamsRecall(
        INamsRecallService recallService,
        [Description("The resolved NAMS conversation ID (from INamsConversationResolver) -- an opaque " +
                      "per-conversation handle, never a raw user or workspace identifier. Identity is " +
                      "established by the host before this tool is ever reachable, not supplied by the model.")]
        string namsConversationId,
        [Description("Optional query text to drive entity search; omit or leave blank to skip it.")]
        string? queryText = null,
        CancellationToken cancellationToken = default)
    {
        // Defensive: a malformed/adversarial MCP call's argument binding isn't guaranteed to enforce
        // non-null for a plain `string` parameter -- this must fail as a clean error response, never an
        // unhandled exception out of the tool invocation.
        if (string.IsNullOrWhiteSpace(namsConversationId))
            return NamsMcpToolJson.Serialize(new { error = "namsConversationId is required." });

        var result = await recallService.RecallAsync(namsConversationId, queryText, cancellationToken).ConfigureAwait(false);
        return NamsMcpToolJson.Serialize(new
        {
            isPartial = result.IsPartial,
            items = result.Items.Select(i => new
            {
                i.SourceId,
                category = i.Category.ToString(),
                i.Content,
                provenance = i.Provenance.ToString(),
                i.Role,
                i.CreatedAt
            }),
            warnings = result.Warnings.Select(w => new { w.Category, w.Message })
        });
    }
}
