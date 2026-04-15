namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Represents an extractor that produces entities from source data.
/// Maps to the Python reference Extractor node.
/// </summary>
public sealed record Extractor
{
    /// <summary>
    /// Unique identifier for the extractor.
    /// </summary>
    public required string ExtractorId { get; init; }

    /// <summary>
    /// Name of the extractor (unique key in Neo4j).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Version string of the extractor.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// JSON-serialized configuration used by the extractor.
    /// </summary>
    public string? ConfigJson { get; init; }

    /// <summary>
    /// UTC timestamp when the extractor was created.
    /// </summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }
}
