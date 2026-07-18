using System.ComponentModel;
using ModelContextProtocol.Server;
using AgentMemory.Nams.Client;

namespace AgentMemory.McpServer.Nams.Tools;

/// <summary>
/// Write NAMS entity tools. Registered only by <c>AddNamsAgentMemoryMcpWriteTools</c> -- a separate, explicit
/// opt-in from <c>AddNamsAgentMemoryMcpTools</c> (the read-only registration), matching the plan's own rule
/// that write tools are never automatically enabled. See <see cref="NamsEntityReadTools"/> for why these
/// resolve <see cref="INamsClient"/> directly rather than through a dedicated public service.
/// </summary>
[McpServerToolType]
internal sealed class NamsEntityWriteTools
{
    [McpServerTool(Name = "nams_create_entity"),
     Description("Explicitly create a NAMS entity. Entities are normally created by the async extraction " +
                  "pipeline -- use this only when an explicit, synchronous write is needed. NAMS's own " +
                  "fuzzy-duplicate resolution may return a different id/shape than the submitted name/type; " +
                  "check \"resolution\" in the result (\"created\", \"review_pending\", or \"merged\").")]
    public static async Task<string> NamsCreateEntity(
        INamsClient client,
        [Description("The entity's name.")] string name,
        [Description("The entity's type (e.g. \"Person\", \"Product\").")] string type,
        [Description("Optional free-text description.")] string? description,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return NamsMcpToolJson.Serialize(new { error = "name is required." });
        if (string.IsNullOrWhiteSpace(type))
            return NamsMcpToolJson.Serialize(new { error = "type is required." });

        var result = await client.CreateEntityAsync(name, type, description, cancellationToken).ConfigureAwait(false);
        return NamsMcpToolJson.Serialize(new
        {
            result.Id,
            result.Resolution,
            result.Name,
            result.Type,
            result.Description,
            result.DuplicateOf,
            result.MergedInto,
            result.Confidence
        });
    }

    [McpServerTool(Name = "nams_entity_feedback"),
     Description("Score and/or confirm an existing NAMS entity.")]
    public static async Task<string> NamsEntityFeedback(
        INamsClient client,
        [Description("The entity id to update.")] string entityId,
        [Description("Optional confidence score between 0 and 1; omit to leave unset.")] double? userScore,
        [Description("Optional human-verified flag; omit to leave unset.")] bool? confirmed,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entityId))
            return NamsMcpToolJson.Serialize(new { updated = false, error = "entityId is required." });

        var result = await client.SetEntityFeedbackAsync(entityId, userScore, confirmed, cancellationToken).ConfigureAwait(false);
        return NamsMcpToolJson.Serialize(new { result.Id, result.Updated });
    }
}
