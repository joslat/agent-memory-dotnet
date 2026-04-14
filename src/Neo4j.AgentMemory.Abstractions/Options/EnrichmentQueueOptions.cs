namespace Neo4j.AgentMemory.Abstractions.Options;

/// <summary>
/// Configuration for the background enrichment queue.
/// </summary>
public record EnrichmentQueueOptions
{
    /// <summary>Maximum concurrent enrichment operations.</summary>
    public int MaxConcurrency { get; init; } = 3;

    /// <summary>Maximum retry attempts per item.</summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>Delay between retries.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum queue capacity. Oldest items are dropped when this limit is exceeded.</summary>
    public int MaxQueueCapacity { get; init; } = 1000;

    /// <summary>Whether to enable the background queue (vs synchronous enrichment).</summary>
    public bool Enabled { get; init; } = true;
}
