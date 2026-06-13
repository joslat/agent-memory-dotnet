using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Services;

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
    /// Gets recent messages for a session. The result is capped at the configured
    /// <c>MaxMessagesPerQuery</c> and ordered newest-first; use it for recall/context, not for
    /// whole-session operations.
    /// </summary>
    Task<IReadOnlyList<Message>> GetRecentMessagesAsync(
        string sessionId,
        int limit = 10,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets ALL messages for a session in chronological (oldest-first) order, with no cap. Intended for
    /// whole-session operations such as retroactive extraction, which must see every message — unlike
    /// <see cref="GetRecentMessagesAsync"/>, which is intentionally capped.
    /// </summary>
    Task<IReadOnlyList<Message>> GetAllSessionMessagesAsync(
        string sessionId,
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

    /// <summary>
    /// Gets recent messages for a session that existed at a specific point in time.
    /// </summary>
    Task<IReadOnlyList<Message>> GetRecentMessagesAsOfAsync(
        string sessionId,
        DateTimeOffset asOf,
        int limit = 10,
        CancellationToken cancellationToken = default);
}
