namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Represents an entity extracted from conversations.
/// </summary>
public sealed record Entity
{
    /// <summary>
    /// Unique identifier for the entity.
    /// </summary>
    public required string EntityId { get; init; }

    /// <summary>
    /// Name of the entity as mentioned.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Canonical or normalized name for deduplication.
    /// </summary>
    public string? CanonicalName { get; init; }

    /// <summary>
    /// Type classification (e.g., "Person", "Organization", "Location").
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Optional subtype for finer classification.
    /// </summary>
    public string? Subtype { get; init; }

    /// <summary>
    /// Description or context about the entity.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Confidence score (0.0 to 1.0) for the extraction.
    /// </summary>
    public required double Confidence { get; init; }

    /// <summary>
    /// Optional embedding vector for semantic search.
    /// </summary>
    public float[]? Embedding { get; init; }

    /// <summary>
    /// Alternative names or aliases.
    /// </summary>
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Additional structured attributes.
    /// </summary>
    public IReadOnlyDictionary<string, object> Attributes { get; init; } =
        new Dictionary<string, object>();

    /// <summary>
    /// Source message references for provenance.
    /// </summary>
    public IReadOnlyList<string> SourceMessageIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// UTC timestamp when the entity was created.
    /// </summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } =
        new Dictionary<string, object>();
}
