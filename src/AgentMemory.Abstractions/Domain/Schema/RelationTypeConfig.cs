namespace AgentMemory.Abstractions.Domain.Schema;

/// <summary>Configuration for a relationship type in the knowledge graph schema.</summary>
public sealed record RelationTypeConfig
{
    /// <summary>The unique name of the relationship type.</summary>
    public required string Name { get; init; }

    /// <summary>An optional human-readable description of the relationship type.</summary>
    public string? Description { get; init; }

    /// <summary>The entity type names allowed as the source (start) of this relationship.</summary>
    public IReadOnlyList<string> SourceTypes { get; init; } = Array.Empty<string>();

    /// <summary>The entity type names allowed as the target (end) of this relationship.</summary>
    public IReadOnlyList<string> TargetTypes { get; init; } = Array.Empty<string>();

    /// <summary>The names of properties that may be stored on this relationship.</summary>
    public IReadOnlyList<string> Properties { get; init; } = Array.Empty<string>();
}
