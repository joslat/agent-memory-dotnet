#pragma warning disable CS1591
namespace Neo4j.AgentMemory.Abstractions.Domain.Schema;

/// <summary>Configuration for a relationship type in the knowledge graph schema.</summary>
public sealed record RelationTypeConfig
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> SourceTypes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> TargetTypes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Properties { get; init; } = Array.Empty<string>();
}
