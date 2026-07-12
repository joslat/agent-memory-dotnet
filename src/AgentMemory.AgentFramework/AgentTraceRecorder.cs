using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.AgentFramework;

/// <summary>
/// Records MAF agent activity as reasoning traces. A thin adapter over IReasoningMemoryService.
/// </summary>
/// <remarks>
/// Persistence is gated by <see cref="AgentFrameworkOptions.PersistReasoningTraces"/> (default off): when
/// disabled the recorder never touches <see cref="IReasoningMemoryService"/> and returns synthetic
/// in-memory records, so a consumer relying on the documented default avoids the Neo4j write overhead.
/// </remarks>
public sealed class AgentTraceRecorder
{
    private readonly IReasoningMemoryService _reasoningService;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private readonly bool _persist;
    private readonly ILogger<AgentTraceRecorder> _logger;

    // Tracks current step count per active trace.
    private readonly ConcurrentDictionary<string, int> _stepCounts = new();

    /// <summary>
    /// Initializes a new <see cref="AgentTraceRecorder"/>.
    /// </summary>
    /// <param name="reasoningService">Service that persists traces and steps to the Neo4j store.</param>
    /// <param name="clock">Provides the current UTC time for trace timestamps.</param>
    /// <param name="idGenerator">Generates unique identifiers for trace and step records.</param>
    /// <param name="options">Agent-framework options; <c>PersistReasoningTraces</c> gates persistence.</param>
    /// <param name="logger">Logger for diagnostics and warnings.</param>
    public AgentTraceRecorder(
        IReasoningMemoryService reasoningService,
        IClock clock,
        IIdGenerator idGenerator,
        IOptions<AgentFrameworkOptions> options,
        ILogger<AgentTraceRecorder> logger)
    {
        _reasoningService = reasoningService ?? throw new ArgumentNullException(nameof(reasoningService));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
        ArgumentNullException.ThrowIfNull(options);
        _persist = options.Value.PersistReasoningTraces;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Starts a new reasoning trace for an agent run.</summary>
    /// <param name="sessionId">Session identifier that scopes this trace to a conversation.</param>
    /// <param name="task">Human-readable description of the task the agent is performing.</param>
    /// <param name="ownerId">Optional owner id that scopes the trace to a user (R1).</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The created <see cref="ReasoningTrace"/> with its assigned <c>TraceId</c>.</returns>
    // Parameter order is (sessionId, task) to match IReasoningMemoryService.StartTraceAsync — the two strings
    // are easy to transpose, and a mismatch here would silently tag traces with the task as the session.
    public async Task<ReasoningTrace> StartTraceAsync(
        string sessionId,
        string task,
        string? ownerId = null,
        CancellationToken cancellationToken = default)
    {
        if (!_persist)
        {
            var synthetic = new ReasoningTrace
            {
                TraceId = _idGenerator.GenerateId(),
                SessionId = sessionId,
                OwnerId = ownerId,
                Task = task,
                StartedAtUtc = _clock.UtcNow
            };
            _stepCounts[synthetic.TraceId] = 0;
            return synthetic;
        }

        var trace = await _reasoningService.StartTraceAsync(
            sessionId, task, ownerId: ownerId, cancellationToken: cancellationToken).ConfigureAwait(false);
        _stepCounts[trace.TraceId] = 0;
        return trace;
    }

    /// <summary>Records a reasoning step within an active trace.</summary>
    /// <param name="traceId">Identifier of the trace returned by <see cref="StartTraceAsync"/>.</param>
    /// <param name="stepType">
    /// Step category. Use <c>"action"</c>, <c>"observation"</c>, or any other value for a thought step.
    /// </param>
    /// <param name="content">Text content of this reasoning step.</param>
    /// <param name="metadata">Optional key-value pairs attached to the step for structured introspection.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The persisted <see cref="ReasoningStep"/>.</returns>
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

        if (!_persist)
        {
            return new ReasoningStep
            {
                StepId = _idGenerator.GenerateId(),
                TraceId = traceId,
                StepNumber = stepNumber,
                Thought = thought,
                Action = action,
                Observation = observation,
                Metadata = metadata ?? new Dictionary<string, object>()
            };
        }

        return await _reasoningService.AddStepAsync(
            traceId, stepNumber, thought, action, observation,
            metadata: metadata, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records a tool call that occurred within a reasoning step.</summary>
    /// <param name="stepId">Identifier of the step in which the tool was invoked.</param>
    /// <param name="toolName">Name of the tool that was called.</param>
    /// <param name="input">Serialized input passed to the tool.</param>
    /// <param name="output">Optional serialized output returned by the tool.</param>
    /// <param name="status">Outcome of the tool call; defaults to <see cref="ToolCallStatus.Success"/>.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The persisted <see cref="ToolCall"/> record.</returns>
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

        if (!_persist)
        {
            return new ToolCall
            {
                ToolCallId = _idGenerator.GenerateId(),
                StepId = stepId,
                ToolName = toolName,
                ArgumentsJson = input,
                ResultJson = output,
                Status = status
            };
        }

        return await _reasoningService.RecordToolCallAsync(
            stepId, toolName, input, output, status,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Marks an active trace as complete and records its final outcome.</summary>
    /// <param name="traceId">Identifier of the trace to complete.</param>
    /// <param name="outcome">Human-readable description of how the agent run concluded.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
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

        if (_persist)
        {
            await _reasoningService.CompleteTraceAsync(
                traceId, outcome, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        _stepCounts.TryRemove(traceId, out _);
    }
}
