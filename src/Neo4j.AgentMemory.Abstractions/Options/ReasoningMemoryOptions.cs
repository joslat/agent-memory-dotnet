namespace Neo4j.AgentMemory.Abstractions.Options;

/// <summary>
/// Configuration for reasoning memory.
/// </summary>
public sealed record ReasoningMemoryOptions
{
    /// <summary>Whether to generate embeddings for task descriptions automatically.</summary>
    public bool GenerateTaskEmbeddings { get; init; } = true;

    /// <summary>Whether to store tool call details.</summary>
    public bool StoreToolCalls { get; init; } = true;

    /// <summary>Maximum number of traces to retain per session.</summary>
    public int? MaxTracesPerSession { get; init; }
}
