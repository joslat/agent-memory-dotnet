namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// An entity extracted from text, before persistence.
/// </summary>
public sealed record ExtractedEntity
{
    /// <summary>
    /// Name of the entity.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Type classification.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Optional subtype.
    /// </summary>
    public string? Subtype { get; init; }

    /// <summary>
    /// Optional description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Confidence score (0.0 to 1.0).
    /// </summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>
    /// Alternative names or aliases.
    /// </summary>
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Additional attributes.
    /// </summary>
    public IReadOnlyDictionary<string, object> Attributes { get; init; } =
        new Dictionary<string, object>();
}
