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
    /// Recalls memory context as it existed at a specific point in time.
    /// Only entities, facts, and preferences that were created on or before <paramref name="asOf"/>
    /// and had not been invalidated by that time are included.
    /// </summary>
    Task<RecallResult> RecallAsOfAsync(
        RecallRequest request,
        DateTimeOffset asOf,
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

    /// <summary>
    /// Retroactively runs the extraction pipeline on all messages in a session
    /// and persists the resulting entities, facts, preferences, and relationships.
    /// </summary>
    Task ExtractFromSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retroactively runs the extraction pipeline on all messages in a conversation
    /// and persists the resulting entities, facts, preferences, and relationships.
    /// </summary>
    Task ExtractFromConversationAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates and persists embeddings for all nodes of the given label that
    /// currently have a null embedding. Processes in batches of <paramref name="batchSize"/>.
    /// Supported labels: <c>Entity</c>, <c>Fact</c>, <c>Preference</c>.
    /// </summary>
    /// <returns>Total number of nodes updated.</returns>
    Task<int> GenerateEmbeddingsBatchAsync(
        string nodeLabel,
        int batchSize = 100,
        CancellationToken cancellationToken = default);
}
