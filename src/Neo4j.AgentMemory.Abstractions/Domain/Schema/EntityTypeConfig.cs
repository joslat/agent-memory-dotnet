#pragma warning disable CS1591
namespace Neo4j.AgentMemory.Abstractions.Domain.Schema;

/// <summary>Configuration for a single entity type in the knowledge graph schema.</summary>
public sealed record EntityTypeConfig
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Subtypes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Attributes { get; init; } = Array.Empty<string>();
    public string? Color { get; init; }
}
