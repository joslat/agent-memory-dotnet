namespace Neo4j.AgentMemory.Abstractions.Domain.Extraction.Streaming;

/// <summary>
/// Complete result from a streaming extraction run over a long document.
/// Contains deduplicated entities and relationships from all chunks, plus statistics.
/// </summary>
public sealed record StreamingExtractionResult
{
    /// <summary>Deduplicated entities across all chunks.</summary>
    public IReadOnlyList<ExtractedEntity> Entities { get; init; } = Array.Empty<ExtractedEntity>();

    /// <summary>Deduplicated relationships across all chunks.</summary>
    public IReadOnlyList<ExtractedRelationship> Relationships { get; init; } =
        Array.Empty<ExtractedRelationship>();

    /// <summary>Per-chunk results in order of processing.</summary>
    public IReadOnlyList<StreamingChunkResult> ChunkResults { get; init; } =
        Array.Empty<StreamingChunkResult>();

    /// <summary>Aggregate statistics for the streaming run.</summary>
    public required StreamingExtractionStats Stats { get; init; }

    /// <summary>Converts this result to a standard <see cref="ExtractionResult"/>.</summary>
    public ExtractionResult ToExtractionResult() =>
        new()
        {
            Entities = Entities,
            Relationships = Relationships
        };
}
