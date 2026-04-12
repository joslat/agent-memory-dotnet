using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.AgentFramework;

/// <summary>
/// Records MAF agent activity as reasoning traces. A thin adapter over IReasoningMemoryService.
/// </summary>
public sealed class AgentTraceRecorder
{
    private readonly IReasoningMemoryService _reasoningService;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private readonly ILogger<AgentTraceRecorder> _logger;

    // Tracks current step count per active trace.
    private readonly ConcurrentDictionary<string, int> _stepCounts = new();

    public AgentTraceRecorder(
        IReasoningMemoryService reasoningService,
        IClock clock,
        IIdGenerator idGenerator,
        ILogger<AgentTraceRecorder> logger)
    {
        _reasoningService = reasoningService;
        _clock = clock;
        _idGenerator = idGenerator;
        _logger = logger;
    }

    /// <summary>Starts a new reasoning trace for an agent run.</summary>
    public async Task<ReasoningTrace> StartTraceAsync(
        string task,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var trace = await _reasoningService.StartTraceAsync(
            sessionId, task, cancellationToken: cancellationToken);
        _stepCounts[trace.TraceId] = 0;
        return trace;
    }

    /// <summary>Records a reasoning step within a trace.</summary>
    public async Task<ReasoningStep> RecordStepAsync(
        string traceId,
        string stepType,
        string content,
        IReadOnlyDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var stepNumber = _stepCounts.AddOrUpdate(traceId, 1, (_, n) => n + 1);

        string? thought = null, action = null, observation = null;
        switch (stepType.ToLowerInvariant())
        {
            case "action":
                action = content;
                break;
            case "observation":
                observation = content;
                break;
            default:
                thought = content;
                break;
        }

        return await _reasoningService.AddStepAsync(
            traceId, stepNumber, thought, action, observation,
            metadata: metadata, cancellationToken: cancellationToken);
    }

    /// <summary>Records a tool call within a reasoning step.</summary>
    public async Task<ToolCall> RecordToolCallAsync(
        string stepId,
        string toolName,
        string input,
        string? output = null,
        ToolCallStatus status = ToolCallStatus.Success,
        CancellationToken cancellationToken = default)
    {
        return await _reasoningService.RecordToolCallAsync(
            stepId, toolName, input, output, status,
            cancellationToken: cancellationToken);
    }

    /// <summary>Completes a trace and sets its outcome.</summary>
    public async Task CompleteTraceAsync(
        string traceId,
        string outcome,
        CancellationToken cancellationToken = default)
    {
        if (!_stepCounts.ContainsKey(traceId))
        {
            _logger.LogWarning(
                "Completing trace {TraceId} that was not started by this recorder.", traceId);
        }

        await _reasoningService.CompleteTraceAsync(
            traceId, outcome, cancellationToken: cancellationToken);
        _stepCounts.TryRemove(traceId, out _);
    }
}
