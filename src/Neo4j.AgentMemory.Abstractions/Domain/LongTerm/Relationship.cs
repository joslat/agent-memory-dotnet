namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Represents a relationship between two entities.
/// </summary>
public sealed record Relationship
{
    /// <summary>
    /// Unique identifier for the relationship.
    /// </summary>
    public required string RelationshipId { get; init; }

    /// <summary>
    /// Source entity identifier.
    /// </summary>
    public required string SourceEntityId { get; init; }

    /// <summary>
    /// Target entity identifier.
    /// </summary>
    public required string TargetEntityId { get; init; }

    /// <summary>
    /// Type of relationship (e.g., "WORKS_FOR", "LOCATED_IN", "KNOWS").
    /// </summary>
    public required string RelationshipType { get; init; }

    /// <summary>
    /// Confidence score (0.0 to 1.0) for the extraction.
    /// </summary>
    public required double Confidence { get; init; }

    /// <summary>
    /// Optional description of the relationship.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Optional start of validity period.
    /// </summary>
    public DateTimeOffset? ValidFrom { get; init; }

    /// <summary>
    /// Optional end of validity period.
    /// </summary>
    public DateTimeOffset? ValidUntil { get; init; }

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
    /// UTC timestamp when the relationship was created.
    /// </summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } =
        new Dictionary<string, object>();
}
