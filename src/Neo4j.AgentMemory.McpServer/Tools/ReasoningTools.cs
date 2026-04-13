using System.ComponentModel;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.McpServer.Tools;

/// <summary>
/// Reasoning trace tools: start, record step, complete.
/// </summary>
[McpServerToolType]
public sealed class ReasoningTools
{
    [McpServerTool(Name = "memory_start_trace"), Description("Start a new reasoning trace to track an agent's multi-step problem solving process.")]
    public static async Task<string> MemoryStartTrace(
        IReasoningMemoryService reasoningMemory,
        IOptions<McpServerOptions> options,
        [Description("Description of the task being solved")] string task,
        [Description("Session identifier (optional, uses default if omitted)")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var sid = sessionId ?? options.Value.DefaultSessionId;
        var trace = await reasoningMemory.StartTraceAsync(sid, task, cancellationToken: cancellationToken);
        return ToolJsonContext.Serialize(new
        {
            trace.TraceId,
            trace.SessionId,
            trace.Task,
            trace.Outcome,
            trace.Success,
            trace.StartedAtUtc,
            trace.CompletedAtUtc
        });
    }

    [McpServerTool(Name = "memory_record_step"), Description("Record a reasoning step within an active trace. Captures thought, action, and observation for each step.")]
    public static async Task<string> MemoryRecordStep(
        IReasoningMemoryService reasoningMemory,
        [Description("The trace ID to add the step to")] string traceId,
        [Description("Step number in the sequence (1-based)")] int stepNumber,
        [Description("The agent's thought or reasoning at this step (optional)")] string? thought = null,
        [Description("The action taken (optional)")] string? action = null,
        [Description("The observation or result from the action (optional)")] string? observation = null,
        CancellationToken cancellationToken = default)
    {
        var step = await reasoningMemory.AddStepAsync(
            traceId, stepNumber, thought, action, observation,
            cancellationToken: cancellationToken);
        return ToolJsonContext.Serialize(new
        {
            step.StepId,
            step.TraceId,
            step.StepNumber,
            step.Thought,
            step.Action,
            step.Observation
        });
    }

    [McpServerTool(Name = "memory_complete_trace"), Description("Complete a reasoning trace, recording the final outcome.")]
    public static async Task<string> MemoryCompleteTrace(
        IReasoningMemoryService reasoningMemory,
        [Description("The trace ID to complete")] string traceId,
        [Description("Description of the outcome (optional)")] string? outcome = null,
        [Description("Whether the task was successful (optional)")] bool? success = null,
        CancellationToken cancellationToken = default)
    {
        var trace = await reasoningMemory.CompleteTraceAsync(traceId, outcome, success, cancellationToken);
        return ToolJsonContext.Serialize(new
        {
            trace.TraceId,
            trace.SessionId,
            trace.Task,
            trace.Outcome,
            trace.Success,
            trace.StartedAtUtc,
            trace.CompletedAtUtc
        });
    }
}
