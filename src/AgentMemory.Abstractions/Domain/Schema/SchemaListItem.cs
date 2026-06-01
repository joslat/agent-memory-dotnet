namespace AgentMemory.Abstractions.Domain.Schema;

/// <summary>Summary of a schema for listing purposes.</summary>
public sealed record SchemaListItem
{
    /// <summary>Unique name identifying the schema.</summary>
    public required string Name { get; init; }

    /// <summary>Version identifier of the most recent schema revision.</summary>
    public required string LatestVersion { get; init; }

    /// <summary>Optional human-readable description of the schema.</summary>
    public string? Description { get; init; }

    /// <summary>Total number of versions that exist for the schema.</summary>
    public int VersionCount { get; init; }

    /// <summary>Indicates whether the schema is currently active.</summary>
    public bool IsActive { get; init; }
}
