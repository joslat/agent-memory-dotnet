using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Abstractions.Repositories;

/// <summary>
/// Repository for reasoning trace persistence.
/// </summary>
public interface IReasoningTraceRepository
{
    /// <summary>Adds a reasoning trace.</summary>
    Task<ReasoningTrace> AddAsync(ReasoningTrace trace, CancellationToken cancellationToken = default);

    /// <summary>Updates a reasoning trace.</summary>
    Task<ReasoningTrace> UpdateAsync(ReasoningTrace trace, CancellationToken cancellationToken = default);

    /// <summary>Gets a trace by identifier.</summary>
    Task<ReasoningTrace?> GetByIdAsync(string traceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists traces for a session, optionally scoped to an owner (R1). A <c>session_id</c> is not a random
    /// handle (it can be shared/guessable), so when <paramref name="scope"/> is set this returns only the
    /// owner's own (and, if <c>IncludeShared</c>, shared/global) traces — never another owner's.
    /// </summary>
    Task<IReadOnlyList<ReasoningTrace>> ListBySessionAsync(string sessionId, int limit = 10, MemoryScope? scope = null, CancellationToken cancellationToken = default);

    /// <summary>Searches traces by task embedding similarity, optionally scoped to an owner (R1).</summary>
    Task<IReadOnlyList<(ReasoningTrace Trace, double Score)>> SearchByTaskVectorAsync(
        float[] taskEmbedding,
        bool? successFilter = null,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Point-in-time variant of <see cref="SearchByTaskVectorAsync"/>: only traces that had started at
    /// or before <paramref name="asOf"/>, optionally scoped to an owner (R1).
    /// </summary>
    Task<IReadOnlyList<(ReasoningTrace Trace, double Score)>> SearchByTaskVectorAsOfAsync(
        float[] taskEmbedding,
        DateTimeOffset asOf,
        bool? successFilter = null,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>Creates an INITIATED_BY relationship from a trace to the message that triggered it.</summary>
    Task CreateInitiatedByRelationshipAsync(string traceId, string messageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates both HAS_TRACE (Conversation→Trace) and IN_SESSION (Trace→Conversation) relationships.
    /// </summary>
    Task CreateConversationTraceRelationshipsAsync(string conversationId, string traceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes ReasoningTrace and child ReasoningStep nodes for a session, confined to a single R1 owner
    /// bucket: when <paramref name="ownerId"/> is null the shared/global bucket only (<c>owner_id IS NULL</c>),
    /// otherwise that owner's own traces. A session-keyed destructive write never crosses owners — owner A's
    /// clear cannot delete owner B's traces, and a null-owner clear never touches an owner's private traces.
    /// </summary>
    Task DeleteBySessionAsync(string sessionId, string? ownerId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enforces a per-session retention cap (H): keeps the newest <paramref name="maxToKeep"/> traces for
    /// the session and hard-deletes older traces together with their child ReasoningStep nodes. Returns the
    /// number of traces pruned. This is a DESTRUCTIVE write, so it ALWAYS confines to exactly one R1 bucket:
    /// when <paramref name="ownerId"/> is set, only that owner's own traces; when it is <c>null</c>, only the
    /// shared/global bucket (<c>owner_id IS NULL</c>) — never another owner's traces, and never "all owners".
    /// Ordering is newest-first, so a just-added trace is always retained.
    /// </summary>
    Task<int> PruneSessionTracesAsync(string sessionId, int maxToKeep, string? ownerId = null, CancellationToken cancellationToken = default);
}
