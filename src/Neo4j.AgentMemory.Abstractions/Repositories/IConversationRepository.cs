using Neo4j.AgentMemory.Abstractions.Domain;

namespace Neo4j.AgentMemory.Abstractions.Repositories;

/// <summary>
/// Repository for conversation persistence.
/// </summary>
public interface IConversationRepository
{
    /// <summary>
    /// Adds or updates a conversation.
    /// </summary>
    Task<Conversation> UpsertAsync(
        Conversation conversation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a conversation by identifier.
    /// </summary>
    Task<Conversation?> GetByIdAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets conversations for a session.
    /// </summary>
    Task<IReadOnlyList<Conversation>> GetBySessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a conversation.
    /// </summary>
    Task DeleteAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all sessions with summary information.
    /// </summary>
    Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(int limit = 50, CancellationToken ct = default);
}
