using Neo4j.AgentMemory.Abstractions.Domain;

namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Facade service for all memory operations.
/// </summary>
public interface IMemoryService
{
    /// <summary>
    /// Recalls memory context for a query.
    /// </summary>
    Task<RecallResult> RecallAsync(
        RecallRequest request,
        CancellationToken cancellationToken = default);

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
    /// Clears all memory for a session.
    /// </summary>
    Task ClearSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);
}
