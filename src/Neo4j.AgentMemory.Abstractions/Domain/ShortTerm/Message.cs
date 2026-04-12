namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Represents a message in a conversation.
/// </summary>
public sealed record Message
{
    /// <summary>
    /// Unique identifier for the message.
    /// </summary>
    public required string MessageId { get; init; }

    /// <summary>
    /// Identifier of the conversation this message belongs to.
    /// </summary>
    public required string ConversationId { get; init; }

    /// <summary>
    /// Session identifier for the message.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Role of the message sender (e.g., "user", "assistant", "system").
    /// </summary>
    public required string Role { get; init; }

    /// <summary>
    /// Text content of the message.
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// UTC timestamp when the message was created.
    /// </summary>
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>
    /// Optional embedding vector for semantic search.
    /// </summary>
    public float[]? Embedding { get; init; }

    /// <summary>
    /// Optional tool call identifiers if this message involved tool usage.
    /// </summary>
    public IReadOnlyList<string>? ToolCallIds { get; init; }

    /// <summary>
    /// Additional metadata for the message.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } =
        new Dictionary<string, object>();
}
