namespace Neo4j.AgentMemory.Abstractions.Domain.Extraction.Streaming;

/// <summary>Statistics from a complete streaming extraction run.</summary>
public sealed record StreamingExtractionStats
{
    /// <summary>Total number of chunks processed.</summary>
    public int TotalChunks { get; init; }

    /// <summary>Number of chunks that succeeded.</summary>
    public int SuccessfulChunks { get; init; }

    /// <summary>Number of chunks that failed.</summary>
    public int FailedChunks { get; init; }

    /// <summary>Raw entity count before deduplication.</summary>
    public int TotalEntities { get; init; }

    /// <summary>Raw relationship count before deduplication.</summary>
    public int TotalRelations { get; init; }

    /// <summary>Entity count after deduplication.</summary>
    public int DeduplicatedEntities { get; init; }

    /// <summary>Total wall-clock time in milliseconds.</summary>
    public double TotalDurationMs { get; init; }

    /// <summary>Total character count of the source document.</summary>
    public int TotalCharacters { get; init; }

    /// <summary>Approximate total token count of the source document.</summary>
    public int TotalTokensApprox { get; init; }
}
