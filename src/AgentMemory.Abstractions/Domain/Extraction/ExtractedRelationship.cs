using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Abstractions.Domain;

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

    /// <summary>
    /// The id of a stored relationship this one ends (married to ends engaged to), as a store-aware writer
    /// (<see cref="Services.IMemoryWriter"/>) named it, or null. Ended at the write (its valid-until set; the edge stays as
    /// history) only when the update judge confirms the pair; otherwise this relationship is simply added.
    /// </summary>
    [Experimental("AMWRITE001")]
    public string? ReplacesId { get; init; }
}
