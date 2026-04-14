namespace Neo4j.AgentMemory.Abstractions.Options;

/// <summary>
/// Configuration for context compression / observational memory.
/// </summary>
public sealed class ContextCompressionOptions
{
    /// <summary>Token threshold that triggers compression (default: 30000).</summary>
    public int TokenThreshold { get; set; } = 30_000;

    /// <summary>Number of recent messages to always keep uncompressed.</summary>
    public int RecentMessageCount { get; set; } = 10;

    /// <summary>Maximum number of observation summaries to generate.</summary>
    public int MaxObservations { get; set; } = 5;

    /// <summary>Whether to generate high-level reflections from observations.</summary>
    public bool EnableReflections { get; set; } = true;
}
