namespace Neo4j.AgentMemory.Abstractions.Domain.Enrichment;

/// <summary>
/// Indicates the outcome of an enrichment attempt.
/// </summary>
public enum EnrichmentStatus
{
    /// <summary>Enrichment data was successfully retrieved.</summary>
    Success,

    /// <summary>No matching entity was found in the knowledge source.</summary>
    NotFound,

    /// <summary>The entity type is not supported by this provider.</summary>
    Skipped,

    /// <summary>The request was rejected due to rate limiting.</summary>
    RateLimited,

    /// <summary>An error occurred during enrichment (API error, timeout, etc.).</summary>
    Error
}
