namespace Neo4j.AgentMemory.Abstractions.Options;

/// <summary>
/// Configuration for memory recall operations.
/// </summary>
public sealed record RecallOptions
{
    /// <summary>Maximum recent messages to include.</summary>
    public int MaxRecentMessages { get; init; } = 10;

    /// <summary>Maximum semantically relevant messages to include.</summary>
    public int MaxRelevantMessages { get; init; } = 5;

    /// <summary>Maximum entities to include.</summary>
    public int MaxEntities { get; init; } = 10;

    /// <summary>Maximum preferences to include.</summary>
    public int MaxPreferences { get; init; } = 5;

    /// <summary>Maximum facts to include.</summary>
    public int MaxFacts { get; init; } = 10;

    /// <summary>Maximum reasoning traces to include.</summary>
    public int MaxTraces { get; init; } = 3;

    /// <summary>Maximum GraphRAG items to include.</summary>
    public int MaxGraphRagItems { get; init; } = 5;

    /// <summary>Minimum similarity score for semantic search (0.0 to 1.0).</summary>
    public double MinSimilarityScore { get; init; } = 0.7;

    /// <summary>Retrieval blend mode.</summary>
    public RetrievalBlendMode BlendMode { get; init; } = RetrievalBlendMode.Blended;

    /// <summary>Default singleton instance.</summary>
    public static RecallOptions Default { get; } = new();
}
