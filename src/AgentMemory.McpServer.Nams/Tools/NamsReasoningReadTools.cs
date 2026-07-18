using System.ComponentModel;
using ModelContextProtocol.Server;
using AgentMemory.Nams.Client;

namespace AgentMemory.McpServer.Nams.Tools;

/// <summary>
/// Read-only NAMS reasoning/provenance tools. Registered by <c>AddNamsAgentMemoryMcpTools</c>. See
/// <see cref="NamsEntityReadTools"/> for why these resolve <see cref="INamsClient"/> directly rather than
/// through a dedicated public service.
/// </summary>
[McpServerToolType]
internal sealed class NamsReasoningReadTools
{
    [McpServerTool(Name = "nams_list_reasoning_steps"),
     Description("List recorded reasoning steps for a NAMS conversation.")]
    public static async Task<string> NamsListReasoningSteps(
        INamsClient client,
        [Description("The resolved NAMS conversation ID.")] string namsConversationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(namsConversationId))
            return NamsMcpToolJson.Serialize(new { error = "namsConversationId is required." });

        var steps = await client.ListReasoningStepsAsync(namsConversationId, cancellationToken).ConfigureAwait(false);
        return NamsMcpToolJson.Serialize(new
        {
            steps = steps.Select(s => new { s.Id, s.ConversationId, s.Reasoning, s.ActionTaken, s.Result, s.CreatedAt })
        });
    }

    [McpServerTool(Name = "nams_reasoning_trace"),
     Description("Get the full reasoning trace for a NAMS conversation -- every recorded step and tool call, " +
                  "in order.")]
    public static async Task<string> NamsReasoningTrace(
        INamsClient client,
        [Description("The resolved NAMS conversation ID.")] string namsConversationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(namsConversationId))
            return NamsMcpToolJson.Serialize(new { error = "namsConversationId is required." });

        var trace = await client.GetReasoningTraceAsync(namsConversationId, cancellationToken).ConfigureAwait(false);
        return NamsMcpToolJson.Serialize(new
        {
            trace.ConversationId,
            steps = trace.Steps.Select(s => new { s.Id, s.ConversationId, s.Reasoning, s.ActionTaken, s.Result, s.CreatedAt }),
            toolCalls = trace.ToolCalls.Select(c => new { c.Id, c.StepId, c.ToolName, c.Status, c.Input, c.Output, c.DurationMs, c.CreatedAt })
        });
    }

    [McpServerTool(Name = "nams_entity_provenance"),
     Description("Get the reasoning chain that influenced an entity's creation, if any was recorded. May be " +
                  "empty -- provenance links are populated asynchronously and are not guaranteed to appear " +
                  "shortly after related reasoning steps are recorded.")]
    public static async Task<string> NamsEntityProvenance(
        INamsClient client,
        [Description("The entity id.")] string entityId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entityId))
            return NamsMcpToolJson.Serialize(new { error = "entityId is required." });

        var provenance = await client.GetEntityProvenanceAsync(entityId, cancellationToken).ConfigureAwait(false);
        return NamsMcpToolJson.Serialize(new { provenance.EntityId, provenance.Provenance });
    }
}
