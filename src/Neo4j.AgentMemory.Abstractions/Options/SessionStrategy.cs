namespace Neo4j.AgentMemory.Abstractions.Options;

/// <summary>
/// Strategy for session scoping.
/// </summary>
public enum SessionStrategy
{
    /// <summary>
    /// One session per conversation.
    /// </summary>
    PerConversation,

    /// <summary>
    /// One session per day.
    /// </summary>
    PerDay,

    /// <summary>
    /// Persistent session per user.
    /// </summary>
    PersistentPerUser
}
