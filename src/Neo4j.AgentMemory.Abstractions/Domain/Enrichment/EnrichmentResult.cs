namespace Neo4j.AgentMemory.Abstractions.Domain.Enrichment;

/// <summary>
/// Result of enriching a named entity from an external knowledge source.
/// </summary>
public sealed record EnrichmentResult
{
    /// <summary>The entity name that was enriched.</summary>
    public required string EntityName { get; init; }
    /// <summary>Short summary or extract from the knowledge source.</summary>
    public string? Summary { get; init; }
    /// <summary>Brief description of the entity.</summary>
    public string? Description { get; init; }
    /// <summary>URL to the entity's Wikipedia page, if available.</summary>
    public string? WikipediaUrl { get; init; }
    /// <summary>URL to a representative image for the entity, if available.</summary>
    public string? ImageUrl { get; init; }
    /// <summary>Additional provider-specific properties.</summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
    /// <summary>Name of the enrichment provider that produced this result.</summary>
    public string? Provider { get; init; }
    /// <summary>UTC timestamp when the enrichment data was retrieved.</summary>
    public DateTimeOffset RetrievedAtUtc { get; init; }
}
