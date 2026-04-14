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

    // ---- Extended properties used by enrichment providers that return richer status info ----

    /// <summary>Outcome status of the enrichment operation (provider-specific; null means not provided).</summary>
    public EnrichmentStatus? Status { get; init; }

    /// <summary>The entity type passed to the provider (e.g. PERSON, ORGANIZATION).</summary>
    public string? EntityType { get; init; }

    /// <summary>Confidence score in [0, 1]. Null if the provider does not supply confidence.</summary>
    public double? Confidence { get; init; }

    /// <summary>Human-readable error message when <see cref="Status"/> indicates failure.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Canonical source URL for the entity (homepage, origin, etc.).</summary>
    public string? SourceUrl { get; init; }

    /// <summary>Provider-specific URI for the entity (e.g. Diffbot DiffbotUri).</summary>
    public string? DiffbotUri { get; init; }

    /// <summary>Additional image URLs beyond the primary <see cref="ImageUrl"/>.</summary>
    public IReadOnlyList<string> Images { get; init; } = Array.Empty<string>();

    /// <summary>Related entities extracted from the provider response.</summary>
    public IReadOnlyList<RelatedEntity> RelatedEntities { get; init; } = Array.Empty<RelatedEntity>();
}
