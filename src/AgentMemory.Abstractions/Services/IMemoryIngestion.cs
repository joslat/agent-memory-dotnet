using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Write-side memory role: adding messages to short-term memory and extracting/persisting long-term
/// memory from them. Depend on this when a component only ingests memory.
/// </summary>
public interface IMemoryIngestion
{
    /// <summary>
    /// Adds a message to short-term memory.
    /// </summary>
    Task<Message> AddMessageAsync(
        string sessionId,
        string conversationId,
        string role,
        string content,
        IReadOnlyDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Batch adds messages to short-term memory.
    /// </summary>
    Task<IReadOnlyList<Message>> AddMessagesAsync(
        IEnumerable<Message> messages,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts and persists long-term memory from messages.
    /// </summary>
    Task<ExtractionResult> ExtractAndPersistAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retroactively runs the extraction pipeline on all messages in a session and persists the
    /// resulting entities, facts, preferences, and relationships. When <paramref name="userId"/> is
    /// supplied (R1) the extracted nodes are owner-stamped and resolution is owner-scoped; null ⇒ the
    /// nodes are stored as shared/global (the prior single-tenant behavior).
    /// </summary>
    Task ExtractFromSessionAsync(
        string sessionId,
        string? userId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retroactively runs the extraction pipeline on all messages in a conversation and persists the
    /// results. When <paramref name="userId"/> is supplied (R1) the extracted nodes are owner-stamped
    /// and resolution is owner-scoped; null ⇒ stored as shared/global.
    /// </summary>
    Task ExtractFromConversationAsync(
        string conversationId,
        string? userId = null,
        CancellationToken cancellationToken = default);
}
