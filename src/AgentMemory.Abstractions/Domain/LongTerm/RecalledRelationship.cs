namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// A relationship recalled with the names of the two entities it joins, so it can be rendered as a sentence
/// ("Rosa, best friend, Carmen") without a second read per endpoint.
/// </summary>
public sealed record RecalledRelationship
{
    /// <summary>The stored relationship.</summary>
    public required Relationship Relationship { get; init; }

    /// <summary>The source entity's name.</summary>
    public required string SourceName { get; init; }

    /// <summary>The target entity's name.</summary>
    public required string TargetName { get; init; }
}
