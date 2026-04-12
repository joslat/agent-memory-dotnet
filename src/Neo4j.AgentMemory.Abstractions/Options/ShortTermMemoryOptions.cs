namespace Neo4j.AgentMemory.Abstractions.Options;

/// <summary>
/// Configuration for short-term memory.
/// </summary>
public sealed record ShortTermMemoryOptions
{
    /// <summary>Whether to generate embeddings for messages automatically.</summary>
    public bool GenerateEmbeddings { get; init; } = true;

    /// <summary>Default number of recent messages to retrieve.</summary>
    public int DefaultRecentMessageLimit { get; init; } = 10;

    /// <summary>Maximum number of messages to retrieve in a single query.</summary>
    public int MaxMessagesPerQuery { get; init; } = 100;

    /// <summary>Session strategy.</summary>
    public SessionStrategy SessionStrategy { get; init; } = SessionStrategy.PerConversation;
}
