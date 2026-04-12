namespace Neo4j.AgentMemory.Abstractions.Options;

/// <summary>
/// Root configuration for the memory system.
/// </summary>
public sealed record MemoryOptions
{
    /// <summary>Short-term memory configuration.</summary>
    public ShortTermMemoryOptions ShortTerm { get; init; } = new();

    /// <summary>Long-term memory configuration.</summary>
    public LongTermMemoryOptions LongTerm { get; init; } = new();

    /// <summary>Reasoning memory configuration.</summary>
    public ReasoningMemoryOptions Reasoning { get; init; } = new();

    /// <summary>Recall configuration.</summary>
    public RecallOptions Recall { get; init; } = RecallOptions.Default;

    /// <summary>Context budget configuration.</summary>
    public ContextBudget ContextBudget { get; init; } = ContextBudget.Default;

    /// <summary>Whether to enable GraphRAG integration.</summary>
    public bool EnableGraphRag { get; init; }

    /// <summary>Whether to enable automatic extraction after message save.</summary>
    public bool EnableAutoExtraction { get; init; } = true;

    /// <summary>Extraction pipeline configuration.</summary>
    public ExtractionOptions Extraction { get; init; } = new();
}
