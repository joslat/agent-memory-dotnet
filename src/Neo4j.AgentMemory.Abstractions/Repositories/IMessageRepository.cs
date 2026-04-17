using Neo4j.AgentMemory.Abstractions.Domain;

namespace Neo4j.AgentMemory.Abstractions.Repositories;

/// <summary>
/// Repository for message persistence.
/// </summary>
public interface IMessageRepository
{
    /// <summary>
    /// Adds a message.
    /// </summary>
    Task<Message> AddAsync(
        Message message,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Batch adds messages.
    /// </summary>
    Task<IReadOnlyList<Message>> AddBatchAsync(
        IEnumerable<Message> messages,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a message by identifier.
    /// </summary>
    Task<Message?> GetByIdAsync(
        string messageId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets messages for a conversation.
    /// </summary>
    Task<IReadOnlyList<Message>> GetByConversationAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets recent messages for a session.
    /// </summary>
    Task<IReadOnlyList<Message>> GetRecentBySessionAsync(
        string sessionId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches messages by vector similarity.
    /// </summary>
    Task<IReadOnlyList<(Message Message, double Score)>> SearchByVectorAsync(
        float[] queryEmbedding,
        string? sessionId = null,
        int limit = 10,
        double minScore = 0.0,
        Dictionary<string, object>? metadataFilters = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all messages for a session.
    /// </summary>
    Task DeleteBySessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a single message by identifier.
    /// When <paramref name="cascade"/> is true, all relationships are removed (DETACH DELETE).
    /// When false, only the node is deleted (relationships must already be removed).
    /// </summary>
    Task<bool> DeleteAsync(string messageId, bool cascade = true, CancellationToken ct = default);
}
