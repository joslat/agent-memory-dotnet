using Neo4j.AgentMemory.Abstractions.Domain.Enrichment;

namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Retrieves structured enrichment data for a named entity from an external knowledge source.
/// </summary>
public interface IEnrichmentService
{
    /// <summary>
    /// Enriches the given entity and returns the result, or <c>null</c> if enrichment data
    /// is unavailable or an error occurs.
    /// </summary>
    /// <summary>
    /// Enriches a named entity with structured data from an external knowledge source.
    /// </summary>
    Task<EnrichmentResult?> EnrichEntityAsync(string entityName, string entityType, CancellationToken ct = default);
}
