namespace Neo4j.AgentMemory.Abstractions.Domain.Enrichment;

/// <summary>
/// A related entity extracted from enrichment data.
/// </summary>
public sealed record RelatedEntity
{
    /// <summary>Display name of the related entity.</summary>
    public required string Name { get; init; }

    /// <summary>The relation type (e.g. "employers", "subsidiaries", "founders").</summary>
    public required string Relation { get; init; }

    /// <summary>Diffbot URI for the related entity, if available.</summary>
    public string? DiffbotUri { get; init; }
}
