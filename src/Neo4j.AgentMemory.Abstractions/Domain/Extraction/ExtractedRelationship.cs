namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// A relationship extracted from text, before persistence.
/// </summary>
public sealed record ExtractedRelationship
{
    /// <summary>
    /// Source entity name.
    /// </summary>
    public required string SourceEntity { get; init; }

    /// <summary>
    /// Target entity name.
    /// </summary>
    public required string TargetEntity { get; init; }

    /// <summary>
    /// Type of relationship.
    /// </summary>
    public required string RelationshipType { get; init; }

    /// <summary>
    /// Optional description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Confidence score (0.0 to 1.0).
    /// </summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>
    /// Additional attributes.
    /// </summary>
    public IReadOnlyDictionary<string, object> Attributes { get; init; } =
        new Dictionary<string, object>();
}
