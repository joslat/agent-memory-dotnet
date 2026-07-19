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
            // NAMS's own response omits conversationId per-step (confirmed live) -- substitute the caller's
            // already-known namsConversationId rather than echoing the (always-null) domain field, the same
            // fix tools/AgentMemory.TckBridge.Nams/Program.cs's get_trace_by_conversation route already applies.
            steps = steps.Select(s => new { s.Id, ConversationId = namsConversationId, s.Reasoning, s.ActionTaken, s.Result, s.CreatedAt })
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
            // NAMS's own response omits conversationId per-step (confirmed live) -- substitute the caller's
            // already-known namsConversationId, same fix as NamsListReasoningSteps above and matching
            // tools/AgentMemory.TckBridge.Nams/Program.cs's get_trace_by_conversation route.
            steps = trace.Steps.Select(s => new { s.Id, ConversationId = namsConversationId, s.Reasoning, s.ActionTaken, s.Result, s.CreatedAt }),
            // SECURITY (partially mitigated): Input/Output are caller-supplied, pre-serialized JSON strings
            // (see INamsClient.RecordToolCallAsync) that can carry arbitrary content -- e.g. the text of a
            // fetched web page an agent tool call returned -- the same untrusted-content risk NamsRecalledItem's
            // SECURITY comment describes for the recall pipeline. Unlike NamsExpandGraph's Message-node
            // elision, blanket-eliding Input/Output would defeat this tool's entire purpose, so instead:
            // delimited+escaped (defeats tag/boundary forgery, NamsUntrustedContentGuard.Delimit) and flagged
            // if they match common instruction-like phrasings (advisory only, content is never redacted).
            // This is NOT the full #92 admission-policy/trust-level pipeline Phase 4-6 built for automatic
            // recall -- deliberately so; that pipeline gates content injected into EVERY turn automatically,
            // whereas this tool's content is only seen if a model/developer explicitly calls it, a narrower
            // exposure this reduced-scope mitigation is proportionate to. If usage ever shows tool-call output
            // regularly carries adopted untrusted content (e.g. an agent that browses the web and records
            // results), revisit whether the fuller pipeline is warranted.
            toolCalls = trace.ToolCalls.Select(c => new
            {
                c.Id,
                c.StepId,
                c.ToolName,
                c.Status,
                Input = NamsUntrustedContentGuard.Delimit(c.Input),
                Output = NamsUntrustedContentGuard.Delimit(c.Output),
                InputFlaggedInstructionLike = NamsUntrustedContentGuard.IsInstructionLike(c.Input),
                OutputFlaggedInstructionLike = NamsUntrustedContentGuard.IsInstructionLike(c.Output),
                c.DurationMs,
                c.CreatedAt
            })
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
