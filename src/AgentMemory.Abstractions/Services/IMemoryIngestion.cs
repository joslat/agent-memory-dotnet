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
    /// Adds a message to short-term memory with a caller-supplied, deterministic id. Calling this twice
    /// with the same <paramref name="messageId"/> returns the pre-existing message unchanged (first-write-
    /// wins) instead of creating a duplicate node -- lets independently-configured persisting components
    /// (e.g. multiple MAF integration components observing the same underlying model response) converge on
    /// one message node when they share a stable identity for it, instead of each minting a fresh one.
    /// </summary>
    /// <remarks>
    /// Default implementation ignores <paramref name="messageId"/> and behaves exactly like
    /// <see cref="AddMessageAsync(string,string,string,string,IReadOnlyDictionary{string,object}?,CancellationToken)"/>
    /// (a fresh id every call) -- implementers of this interface are not required to override this member.
    /// </remarks>
    Task<Message> AddMessageWithIdAsync(
        string sessionId,
        string conversationId,
        string role,
        string content,
        string messageId,
        IReadOnlyDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default) =>
        AddMessageAsync(sessionId, conversationId, role, content, metadata, cancellationToken);

    /// <summary>
    /// Batch adds messages to short-term memory.
    /// </summary>
    Task<IReadOnlyList<Message>> AddMessagesAsync(
        IEnumerable<Message> messages,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts and persists long-term memory from messages. The returned <see cref="ExtractionResult"/>
    /// carries structured per-item outcomes and an overall <see cref="IngestionStatus"/> (#101). Under the
    /// default <c>ExtractionOptions.FailureMode</c> (<c>IngestionFailureMode.BestEffort</c>) this never
    /// throws for a per-item failure; under <c>IngestionFailureMode.FailFast</c> it throws
    /// <see cref="AgentMemory.Abstractions.Exceptions.MemoryIngestionException"/> at the first one,
    /// carrying every outcome completed before the failure.
    /// </summary>
    Task<ExtractionResult> ExtractAndPersistAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retroactively runs the extraction pipeline on all messages in a session and persists the
    /// resulting entities, facts, preferences, and relationships. When <paramref name="userId"/> is
    /// supplied (R1) the extracted nodes are owner-stamped and resolution is owner-scoped; null ⇒ the
    /// nodes are stored as shared/global (the prior single-tenant behavior). Under
    /// <c>IngestionFailureMode.FailFast</c> (#101) can throw
    /// <see cref="AgentMemory.Abstractions.Exceptions.MemoryIngestionException"/>; this method's <c>void</c>
    /// return means the per-item outcomes are only observable via that exception, not on success.
    /// </summary>
    Task ExtractFromSessionAsync(
        string sessionId,
        string? userId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retroactively runs the extraction pipeline on all messages in a conversation and persists the
    /// results. When <paramref name="userId"/> is supplied (R1) the extracted nodes are owner-stamped
    /// and resolution is owner-scoped; null ⇒ stored as shared/global. Under
    /// <c>IngestionFailureMode.FailFast</c> (#101) can throw
    /// <see cref="AgentMemory.Abstractions.Exceptions.MemoryIngestionException"/>; this method's <c>void</c>
    /// return means the per-item outcomes are only observable via that exception, not on success.
    /// </summary>
    Task ExtractFromConversationAsync(
        string conversationId,
        string? userId = null,
        CancellationToken cancellationToken = default);
}
