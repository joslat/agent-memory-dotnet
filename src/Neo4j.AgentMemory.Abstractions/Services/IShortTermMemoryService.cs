using Neo4j.AgentMemory.Abstractions.Domain;

namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Service for short-term (conversational) memory operations.
/// </summary>
public interface IShortTermMemoryService
{
    /// <summary>
    /// Adds a conversation.
    /// </summary>
    Task<Conversation> AddConversationAsync(
        string conversationId,
        string sessionId,
        string? userId = null,
        IReadOnlyDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a message to a conversation.
    /// </summary>
    Task<Message> AddMessageAsync(
        Message message,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Batch adds messages.
    /// </summary>
    Task<IReadOnlyList<Message>> AddMessagesAsync(
        IEnumerable<Message> messages,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets recent messages for a session.
    /// </summary>
    Task<IReadOnlyList<Message>> GetRecentMessagesAsync(
        string sessionId,
        int limit = 10,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets messages for a conversation.
    /// </summary>
    Task<IReadOnlyList<Message>> GetConversationMessagesAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches messages semantically.
    /// </summary>
    Task<IReadOnlyList<Message>> SearchMessagesAsync(
        string? sessionId,
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all messages for a session.
    /// </summary>
    Task ClearSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);
}
