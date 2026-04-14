namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Represents a conversation session containing messages.
/// </summary>
public sealed record Conversation
{
    /// <summary>
    /// Unique identifier for the conversation.
    /// </summary>
    public required string ConversationId { get; init; }

    /// <summary>
    /// Session identifier for grouping related conversations.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Optional user identifier.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>
    /// UTC timestamp when the conversation was created.
    /// </summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>
    /// UTC timestamp when the conversation was last updated.
    /// </summary>
    public required DateTimeOffset UpdatedAtUtc { get; init; }

    /// <summary>
    /// Optional human-readable title for the conversation.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Additional metadata for the conversation.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } =
        new Dictionary<string, object>();
}
