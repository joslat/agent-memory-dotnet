using Neo4j.AgentMemory.Abstractions.Domain;

namespace Neo4j.AgentMemory.Abstractions.Repositories;

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

    /// <summary>Lists traces for a session.</summary>
    Task<IReadOnlyList<ReasoningTrace>> ListBySessionAsync(string sessionId, int limit = 10, CancellationToken cancellationToken = default);

    /// <summary>Searches traces by task embedding similarity.</summary>
    Task<IReadOnlyList<(ReasoningTrace Trace, double Score)>> SearchByTaskVectorAsync(
        float[] taskEmbedding,
        bool? successFilter = null,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default);

    /// <summary>Creates an INITIATED_BY relationship from a trace to the message that triggered it.</summary>
    Task CreateInitiatedByRelationshipAsync(string traceId, string messageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates both HAS_TRACE (Conversation→Trace) and IN_SESSION (Trace→Conversation) relationships.
    /// </summary>
    Task CreateConversationTraceRelationshipsAsync(string conversationId, string traceId, CancellationToken cancellationToken = default);
}
