namespace AgentMemory.Abstractions.Domain.Schema;

/// <summary>Configuration for a single entity type in the knowledge graph schema.</summary>
public sealed record EntityTypeConfig
{
    /// <summary>The unique name of the entity type.</summary>
    public required string Name { get; init; }

    /// <summary>An optional human-readable description of the entity type.</summary>
    public string? Description { get; init; }

    /// <summary>The list of subtype names defined for this entity type.</summary>
    public IReadOnlyList<string> Subtypes { get; init; } = Array.Empty<string>();

    /// <summary>The list of attribute names associated with this entity type.</summary>
    public IReadOnlyList<string> Attributes { get; init; } = Array.Empty<string>();

    /// <summary>An optional display color for the entity type.</summary>
    public string? Color { get; init; }
}
