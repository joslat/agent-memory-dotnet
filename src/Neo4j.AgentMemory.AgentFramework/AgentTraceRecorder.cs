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
        _reasoningService = reasoningService ?? throw new ArgumentNullException(nameof(reasoningService));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Starts a new reasoning trace for an agent run.</summary>
    public async Task<ReasoningTrace> StartTraceAsync(
        string task,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var trace = await _reasoningService.StartTraceAsync(
            sessionId, task, cancellationToken: cancellationToken).ConfigureAwait(false);
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
        if (traceId is null) throw new ArgumentNullException(nameof(traceId));
        if (stepType is null) throw new ArgumentNullException(nameof(stepType));
        if (content is null) throw new ArgumentNullException(nameof(content));

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
            metadata: metadata, cancellationToken: cancellationToken).ConfigureAwait(false);
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
        if (stepId is null) throw new ArgumentNullException(nameof(stepId));
        if (toolName is null) throw new ArgumentNullException(nameof(toolName));
        if (input is null) throw new ArgumentNullException(nameof(input));

        return await _reasoningService.RecordToolCallAsync(
            stepId, toolName, input, output, status,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Completes a trace and sets its outcome.</summary>
    public async Task CompleteTraceAsync(
        string traceId,
        string outcome,
        CancellationToken cancellationToken = default)
    {
        if (traceId is null) throw new ArgumentNullException(nameof(traceId));
        if (outcome is null) throw new ArgumentNullException(nameof(outcome));

        if (!_stepCounts.ContainsKey(traceId))
        {
            _logger.LogWarning(
                "Completing trace {TraceId} that was not started by this recorder.", traceId);
        }

        await _reasoningService.CompleteTraceAsync(
            traceId, outcome, cancellationToken: cancellationToken).ConfigureAwait(false);
        _stepCounts.TryRemove(traceId, out _);
    }
}
