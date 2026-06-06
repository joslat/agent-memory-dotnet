using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Service for reasoning trace memory operations.
/// </summary>
public interface IReasoningMemoryService
{
    /// <summary>
    /// Starts a new reasoning trace. <paramref name="ownerId"/> scopes the trace to a user (R1;
    /// null = shared/global).
    /// </summary>
    Task<ReasoningTrace> StartTraceAsync(
        string sessionId,
        string task,
        float[]? taskEmbedding = null,
        IReadOnlyDictionary<string, object>? metadata = null,
        string? ownerId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a reasoning step to a trace.
    /// </summary>
    Task<ReasoningStep> AddStepAsync(
        string traceId,
        int stepNumber,
        string? thought = null,
        string? action = null,
        string? observation = null,
        float[]? embedding = null,
        IReadOnlyDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a tool call for a step.
    /// </summary>
    Task<ToolCall> RecordToolCallAsync(
        string stepId,
        string toolName,
        string argumentsJson,
        string? resultJson = null,
        ToolCallStatus status = ToolCallStatus.Pending,
        long? durationMs = null,
        string? error = null,
        IReadOnlyDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that a reasoning step read or acted upon the given entities, writing <c>:TOUCHED</c>
    /// audit edges from the step to each existing entity (by id). Entity ids that do not resolve — and
    /// a non-existent step — are silently skipped. Idempotent. Returns the number of entities linked.
    /// </summary>
    Task<int> RecordTouchedEntitiesAsync(
        string stepId,
        IReadOnlyList<string> entityIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the ids of all entities a reasoning step touched, for auditability/provenance.
    /// </summary>
    Task<IReadOnlyList<string>> GetTouchedEntitiesAsync(
        string stepId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes a reasoning trace.
    /// </summary>
    Task<ReasoningTrace> CompleteTraceAsync(
        string traceId,
        string? outcome = null,
        bool? success = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a trace with all its steps.
    /// </summary>
    Task<(ReasoningTrace Trace, IReadOnlyList<ReasoningStep> Steps)> GetTraceWithStepsAsync(
        string traceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists traces for a session.
    /// </summary>
    Task<IReadOnlyList<ReasoningTrace>> ListTracesAsync(
        string sessionId,
        int limit = 10,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches for similar traces by task embedding.
    /// </summary>
    Task<IReadOnlyList<ReasoningTrace>> SearchSimilarTracesAsync(
        float[] taskEmbedding,
        bool? successFilter = null,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);
}
