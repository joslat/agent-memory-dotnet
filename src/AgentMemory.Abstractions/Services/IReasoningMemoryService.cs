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
        int limit = 50,
        int offset = 0,
        MemoryScope? scope = null,
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
    /// Promotes a completed trace to a reusable procedure, or demotes it back to an episode (7.1).
    /// Returns <c>null</c> if the trace no longer exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Until this existed, a consumer could not promote anything.</b> The whole procedural tier —
    /// <c>trace_kind</c>, its index, the procedures-only filter, the prune exemption, eight
    /// live-database tests — sat behind a repository the DI container does not hand out, so the
    /// feature was complete in Cypher and unreachable from the public surface.
    /// </para>
    /// <para>
    /// Deliberately a separate operation from <c>CompleteTraceAsync</c>: completion writes the whole
    /// object, so promoting through it would let a later completion built from a stale in-memory copy
    /// silently demote a procedure back to an episode.
    /// </para>
    /// </remarks>
    Task<ReasoningTrace?> PromoteTraceAsync(
        string traceId,
        TraceKind kind,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            $"{GetType().Name} does not support trace promotion.");

    /// <summary>
    /// Task-similarity search restricted to (or excluding) promoted procedures.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The filter existed at every layer except this one.</b> The repository has taken a
    /// <c>proceduresOnly</c> argument since 7.3; the service passed a hardcoded <c>null</c>, so no
    /// shipped recall path could ask for procedures — an agent looking for "how did I do this before"
    /// got episodes back, which is the wrong precedent library.
    /// </para>
    /// <para>
    /// A default interface method, because the surface is SemVer-locked, and the default forwards to
    /// the unfiltered overload: a store with no promotion concept keeps working and simply does not
    /// filter, rather than appearing to.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<ReasoningTrace>> SearchSimilarTracesAsync(
        float[] taskEmbedding,
        bool? proceduresOnly,
        bool? successFilter,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default) =>
        SearchSimilarTracesAsync(taskEmbedding, successFilter, limit, minScore, scope, cancellationToken);

    /// <summary>
    /// Point-in-time variant of <c>SearchSimilarTracesAsync</c>: only traces that had started at
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
