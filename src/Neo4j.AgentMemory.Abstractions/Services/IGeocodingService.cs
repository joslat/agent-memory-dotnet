using Neo4j.AgentMemory.Abstractions.Domain.Enrichment;

namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Resolves a free-text location string to geographic coordinates.
/// </summary>
public interface IGeocodingService
{
    /// <summary>
    /// Geocodes the given location text and returns the result, or <c>null</c> if the
    /// location cannot be resolved or an error occurs.
    /// </summary>
    /// <summary>
    /// Resolves a free-text location string to geographic coordinates.
    /// </summary>
    Task<GeocodingResult?> GeocodeAsync(string locationText, CancellationToken ct = default);
}
