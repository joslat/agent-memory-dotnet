using Microsoft.Extensions.Logging;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Exceptions;
using Neo4j.AgentMemory.Abstractions.Repositories;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Core.Services;

/// <summary>
/// Service for reasoning trace memory operations.
/// </summary>
public sealed class ReasoningMemoryService : IReasoningMemoryService
{
    private readonly IReasoningTraceRepository _traceRepo;
    private readonly IReasoningStepRepository _stepRepo;
    private readonly IToolCallRepository _toolCallRepo;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private readonly ILogger<ReasoningMemoryService> _logger;

    public ReasoningMemoryService(
        IReasoningTraceRepository traceRepo,
        IReasoningStepRepository stepRepo,
        IToolCallRepository toolCallRepo,
        IClock clock,
        IIdGenerator idGenerator,
        ILogger<ReasoningMemoryService> logger)
    {
        _traceRepo = traceRepo;
        _stepRepo = stepRepo;
        _toolCallRepo = toolCallRepo;
        _clock = clock;
        _idGenerator = idGenerator;
        _logger = logger;
    }

    public async Task<ReasoningTrace> StartTraceAsync(
        string sessionId,
        string task,
        float[]? taskEmbedding = null,
        IReadOnlyDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var trace = new ReasoningTrace
        {
            TraceId = _idGenerator.GenerateId(),
            SessionId = sessionId,
            Task = task,
            TaskEmbedding = taskEmbedding,
            StartedAtUtc = _clock.UtcNow,
            Metadata = metadata ?? new Dictionary<string, object>()
        };

        _logger.LogDebug("Starting trace {TraceId} for session {SessionId}", trace.TraceId, sessionId);
        return await _traceRepo.AddAsync(trace, cancellationToken);
    }

    public async Task<ReasoningStep> AddStepAsync(
        string traceId,
        int stepNumber,
        string? thought = null,
        string? action = null,
        string? observation = null,
        float[]? embedding = null,
        IReadOnlyDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var step = new ReasoningStep
        {
            StepId = _idGenerator.GenerateId(),
            TraceId = traceId,
            StepNumber = stepNumber,
            Thought = thought,
            Action = action,
            Observation = observation,
            Embedding = embedding,
            Metadata = metadata ?? new Dictionary<string, object>()
        };

        _logger.LogDebug("Adding step {StepNumber} to trace {TraceId}", stepNumber, traceId);
        return await _stepRepo.AddAsync(step, cancellationToken);
    }

    public async Task<ToolCall> RecordToolCallAsync(
        string stepId,
        string toolName,
        string argumentsJson,
        string? resultJson = null,
        ToolCallStatus status = ToolCallStatus.Pending,
        long? durationMs = null,
        string? error = null,
        IReadOnlyDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var toolCall = new ToolCall
        {
            ToolCallId = _idGenerator.GenerateId(),
            StepId = stepId,
            ToolName = toolName,
            ArgumentsJson = argumentsJson,
            ResultJson = resultJson,
            Status = status,
            DurationMs = durationMs,
            Error = error,
            Metadata = metadata ?? new Dictionary<string, object>()
        };

        _logger.LogDebug("Recording tool call {ToolName} for step {StepId}", toolName, stepId);
        return await _toolCallRepo.AddAsync(toolCall, cancellationToken);
    }

    public async Task<ReasoningTrace> CompleteTraceAsync(
        string traceId,
        string? outcome = null,
        bool? success = null,
        CancellationToken cancellationToken = default)
    {
        var existing = await _traceRepo.GetByIdAsync(traceId, cancellationToken)
            ?? throw MemoryError.Create($"Trace '{traceId}' not found.")
                .WithCode(MemoryErrorCodes.TraceNotFound)
                .WithMetadata("traceId", traceId)
                .Build();

        var completed = existing with
        {
            Outcome = outcome,
            Success = success,
            CompletedAtUtc = _clock.UtcNow
        };

        _logger.LogDebug("Completing trace {TraceId}, success={Success}", traceId, success);
        return await _traceRepo.UpdateAsync(completed, cancellationToken);
    }

    public async Task<(ReasoningTrace Trace, IReadOnlyList<ReasoningStep> Steps)> GetTraceWithStepsAsync(
        string traceId,
        CancellationToken cancellationToken = default)
    {
        var traceTask = _traceRepo.GetByIdAsync(traceId, cancellationToken);
        var stepsTask = _stepRepo.GetByTraceAsync(traceId, cancellationToken);

        await Task.WhenAll(traceTask, stepsTask);

        var trace = await traceTask
            ?? throw MemoryError.Create($"Trace '{traceId}' not found.")
                .WithCode(MemoryErrorCodes.TraceNotFound)
                .WithMetadata("traceId", traceId)
                .Build();
        var steps = await stepsTask;

        return (trace, steps);
    }

    public Task<IReadOnlyList<ReasoningTrace>> ListTracesAsync(
        string sessionId,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        return _traceRepo.ListBySessionAsync(sessionId, limit, cancellationToken);
    }

    public async Task<IReadOnlyList<ReasoningTrace>> SearchSimilarTracesAsync(
        float[] taskEmbedding,
        bool? successFilter = null,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default)
    {
        var scored = await _traceRepo.SearchByTaskVectorAsync(
            taskEmbedding, successFilter, limit, minScore, cancellationToken);
        return scored.Select(r => r.Trace).ToList();
    }
}
