using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Core.Services;

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
    private readonly ReasoningMemoryOptions _options;
    private readonly ILogger<ReasoningMemoryService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReasoningMemoryService"/> class.
    /// </summary>
    public ReasoningMemoryService(
        IReasoningTraceRepository traceRepo,
        IReasoningStepRepository stepRepo,
        IToolCallRepository toolCallRepo,
        IClock clock,
        IIdGenerator idGenerator,
        IOptions<ReasoningMemoryOptions> options,
        ILogger<ReasoningMemoryService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _traceRepo = traceRepo;
        _stepRepo = stepRepo;
        _toolCallRepo = toolCallRepo;
        _clock = clock;
        _idGenerator = idGenerator;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<ReasoningTrace> StartTraceAsync(
        string sessionId,
        string task,
        float[]? taskEmbedding = null,
        IReadOnlyDictionary<string, object>? metadata = null,
        string? ownerId = null,
        CancellationToken cancellationToken = default)
    {
        var trace = new ReasoningTrace
        {
            TraceId = _idGenerator.GenerateId(),
            SessionId = sessionId,
            OwnerId = ownerId,
            Task = task,
            TaskEmbedding = taskEmbedding,
            StartedAtUtc = _clock.UtcNow,
            Metadata = metadata ?? new Dictionary<string, object>()
        };

        _logger.LogDebug("Starting trace {TraceId} for session {SessionId}", trace.TraceId, sessionId);
        var added = await _traceRepo.AddAsync(trace, cancellationToken);

        // Retention cap (H): when MaxTracesPerSession is configured, prune older traces beyond the cap so a
        // session's reasoning history cannot grow without bound. The prune is owner-scoped (R1) when an owner
        // is present — own-only, so one owner's traces can never evict another owner's (or shared) traces.
        if (_options.MaxTracesPerSession is { } cap && cap > 0)
        {
            var scope = ownerId is null ? null : MemoryScope.For(ownerId, includeShared: false);
            try
            {
                var pruned = await _traceRepo.PruneSessionTracesAsync(sessionId, cap, scope, cancellationToken);
                if (pruned > 0)
                    _logger.LogDebug("Pruned {Pruned} trace(s) beyond MaxTracesPerSession={Cap} for session {SessionId}.",
                        pruned, cap, sessionId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Retention is best-effort housekeeping; never fail the StartTrace call because a prune failed.
                _logger.LogWarning(ex, "Failed to prune traces for session {SessionId} (cap {Cap}).", sessionId, cap);
            }
        }

        return added;
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public Task<int> RecordTouchedEntitiesAsync(
        string stepId,
        IReadOnlyList<string> entityIds,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stepId))
            throw new ArgumentException("Step id must be provided.", nameof(stepId));

        if (entityIds is null || entityIds.Count == 0)
            return Task.FromResult(0);

        _logger.LogDebug(
            "Recording {Count} touched entit(y/ies) for step {StepId}", entityIds.Count, stepId);
        return _stepRepo.LinkTouchedEntitiesAsync(stepId, entityIds, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<string>> GetTouchedEntitiesAsync(
        string stepId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stepId))
            throw new ArgumentException("Step id must be provided.", nameof(stepId));

        return _stepRepo.GetTouchedEntityIdsAsync(stepId, cancellationToken);
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public Task<IReadOnlyList<ReasoningTrace>> ListTracesAsync(
        string sessionId,
        int limit = 10,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        return _traceRepo.ListBySessionAsync(sessionId, limit, scope, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ReasoningTrace>> SearchSimilarTracesAsync(
        float[] taskEmbedding,
        bool? successFilter = null,
        int limit = 10,
        double minScore = 0.0,
        AgentMemory.Abstractions.Options.MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        var scored = await _traceRepo.SearchByTaskVectorAsync(
            taskEmbedding, successFilter, limit, minScore, scope, cancellationToken);
        return scored.Select(r => r.Trace).ToList();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ReasoningTrace>> SearchSimilarTracesAsOfAsync(
        float[] taskEmbedding,
        DateTimeOffset asOf,
        bool? successFilter = null,
        int limit = 10,
        double minScore = 0.0,
        AgentMemory.Abstractions.Options.MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        var scored = await _traceRepo.SearchByTaskVectorAsOfAsync(
            taskEmbedding, asOf, successFilter, limit, minScore, scope, cancellationToken);
        return scored.Select(r => r.Trace).ToList();
    }
}
