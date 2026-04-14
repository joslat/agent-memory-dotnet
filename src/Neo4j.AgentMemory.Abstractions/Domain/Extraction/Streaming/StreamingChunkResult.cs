namespace Neo4j.AgentMemory.Abstractions.Domain.Extraction.Streaming;

/// <summary>Result from extracting entities out of a single streaming chunk.</summary>
public sealed record StreamingChunkResult
{
    /// <summary>The chunk that was processed.</summary>
    public required ChunkInfo Chunk { get; init; }

    /// <summary>The extraction result for this chunk.</summary>
    public required ExtractionResult Result { get; init; }

    /// <summary>Whether extraction succeeded for this chunk.</summary>
    public bool Success { get; init; } = true;

    /// <summary>Error message if extraction failed; null on success.</summary>
    public string? Error { get; init; }

    /// <summary>Wall-clock time in milliseconds to process this chunk.</summary>
    public double DurationMs { get; init; }

    /// <summary>Number of entities extracted from this chunk.</summary>
    public int EntityCount => Result.Entities.Count;

    /// <summary>Number of relationships extracted from this chunk.</summary>
    public int RelationCount => Result.Relationships.Count;
}
