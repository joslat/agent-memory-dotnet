namespace Neo4j.AgentMemory.Abstractions.Options;

/// <summary>
/// Configuration for long-term memory.
/// </summary>
public sealed record LongTermMemoryOptions
{
    /// <summary>Whether to generate embeddings for entities automatically.</summary>
    public bool GenerateEntityEmbeddings { get; init; } = true;

    /// <summary>Whether to generate embeddings for facts automatically.</summary>
    public bool GenerateFactEmbeddings { get; init; } = true;

    /// <summary>Whether to generate embeddings for preferences automatically.</summary>
    public bool GeneratePreferenceEmbeddings { get; init; } = true;

    /// <summary>Minimum confidence threshold for persisting extracted items.</summary>
    public double MinConfidenceThreshold { get; init; } = 0.5;

    /// <summary>Whether to enable entity resolution and deduplication.</summary>
    public bool EnableEntityResolution { get; init; } = true;
}
