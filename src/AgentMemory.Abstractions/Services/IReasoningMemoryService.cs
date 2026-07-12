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
    /// Lists traces for a session, optionally scoped to an owner (R1). When <paramref name="scope"/> is
    /// set, returns only the owner's own (and optionally shared) traces — a session id is not a private
    /// handle, so a multi-row list keyed by it is owner-filtered.
    /// </summary>
    Task<IReadOnlyList<ReasoningTrace>> ListTracesAsync(
        string sessionId,
        int limit = 10,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists reasoning traces across all sessions, newest first, optionally scoped to an owner (R1) and
    /// paged. A cross-session trace list is not keyed by a private handle, so when <paramref name="scope"/>
    /// is set this returns only the owner's own (and, if <c>IncludeShared</c>, shared/global) traces. The
    /// result carries a <c>HasNextPage</c> flag (N+1 pagination); advance with <paramref name="offset"/>.
    /// </summary>
    Task<PagedResult<ReasoningTrace>> ListAllTracesAsync(
        MemoryScope? scope = null,
        int limit = 50,
        int offset = 0,
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

    /// <summary>
    /// Point-in-time variant of <see cref="SearchSimilarTracesAsync"/>: only traces that had started at
    /// or before <paramref name="asOf"/>. Completes temporal recall (entities/facts/preferences already
    /// have point-in-time search) so <c>AssembleContextAsOfAsync</c> can include reasoning traces.
    /// </summary>
    Task<IReadOnlyList<ReasoningTrace>> SearchSimilarTracesAsOfAsync(
        float[] taskEmbedding,
        DateTimeOffset asOf,
        bool? successFilter = null,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);
}
