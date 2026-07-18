using System.ComponentModel;
using ModelContextProtocol.Server;
using AgentMemory.Nams.Client;

namespace AgentMemory.McpServer.Nams.Tools;

/// <summary>
/// Write NAMS reasoning/provenance tools. Registered only by <c>AddNamsAgentMemoryMcpWriteTools</c> -- a
/// separate, explicit opt-in from <c>AddNamsAgentMemoryMcpTools</c>, matching the plan's own rule that write
/// tools are never automatically enabled. See <see cref="NamsEntityReadTools"/> for why these resolve
/// <see cref="INamsClient"/> directly rather than through a dedicated public service.
/// </summary>
[McpServerToolType]
internal sealed class NamsReasoningWriteTools
{
    [McpServerTool(Name = "nams_record_reasoning_step"),
     Description("Record a reasoning step for a NAMS conversation's provenance trail.")]
    public static async Task<string> NamsRecordReasoningStep(
        INamsClient client,
        [Description("The resolved NAMS conversation ID.")] string namsConversationId,
        [Description("The reasoning text.")] string reasoning,
        [Description("The action taken as a result of this reasoning.")] string actionTaken,
        [Description("Optional outcome/result text.")] string? result,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(namsConversationId))
            return NamsMcpToolJson.Serialize(new { error = "namsConversationId is required." });
        if (string.IsNullOrWhiteSpace(reasoning))
            return NamsMcpToolJson.Serialize(new { error = "reasoning is required." });
        if (string.IsNullOrWhiteSpace(actionTaken))
            return NamsMcpToolJson.Serialize(new { error = "actionTaken is required." });

        var step = await client.RecordReasoningStepAsync(namsConversationId, reasoning, actionTaken, result, cancellationToken).ConfigureAwait(false);
        return NamsMcpToolJson.Serialize(new { step.Id, step.ConversationId, step.Reasoning, step.ActionTaken, step.Result, step.CreatedAt });
    }

    [McpServerTool(Name = "nams_record_tool_call"),
     Description("Record a tool call, optionally linked to a reasoning step. input/output must be JSON-encoded " +
                  "strings, not raw objects -- NAMS stores them as scalar string properties.")]
    public static async Task<string> NamsRecordToolCall(
        INamsClient client,
        [Description("Optional reasoning step id this tool call belongs to.")] string? stepId,
        [Description("The tool's name.")] string toolName,
        [Description("The tool call's input, as a JSON-encoded string.")] string input,
        [Description("Optional output, as a JSON-encoded string.")] string? output,
        [Description("Optional status (e.g. \"success\", \"error\").")] string? status,
        [Description("Optional duration in milliseconds.")] int? durationMs,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return NamsMcpToolJson.Serialize(new { error = "toolName is required." });
        if (string.IsNullOrWhiteSpace(input))
            return NamsMcpToolJson.Serialize(new { error = "input is required." });

        var call = await client.RecordToolCallAsync(stepId, toolName, input, output, status, durationMs, cancellationToken).ConfigureAwait(false);
        return NamsMcpToolJson.Serialize(new { call.Id, call.StepId, call.ToolName, call.Status, call.Input, call.Output, call.DurationMs, call.CreatedAt });
    }
}
