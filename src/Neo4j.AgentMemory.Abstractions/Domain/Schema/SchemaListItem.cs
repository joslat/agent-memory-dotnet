#pragma warning disable CS1591
namespace Neo4j.AgentMemory.Abstractions.Domain.Schema;

/// <summary>Summary of a schema for listing purposes.</summary>
public sealed record SchemaListItem
{
    public required string Name { get; init; }
    public required string LatestVersion { get; init; }
    public string? Description { get; init; }
    public int VersionCount { get; init; }
    public bool IsActive { get; init; }
}
