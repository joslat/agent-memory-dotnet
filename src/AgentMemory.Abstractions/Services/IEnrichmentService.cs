using AgentMemory.Abstractions.Domain.Enrichment;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Retrieves structured enrichment data for a named entity from an external knowledge source.
/// </summary>
public interface IEnrichmentService
{
    /// <summary>
    /// Enriches a named entity with structured data from an external knowledge source.
    /// </summary>
    /// <param name="entityName">The entity name to look up.</param>
    /// <param name="entityType">The entity's type (e.g. PERSON, ORGANIZATION), used to disambiguate.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The enrichment result, or <c>null</c> if data is unavailable or an error occurs.</returns>
    Task<EnrichmentResult?> EnrichEntityAsync(string entityName, string entityType, CancellationToken cancellationToken = default);
}
