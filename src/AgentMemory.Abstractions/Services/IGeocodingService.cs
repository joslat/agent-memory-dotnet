using AgentMemory.Abstractions.Domain.Enrichment;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Resolves a free-text location string to geographic coordinates.
/// </summary>
public interface IGeocodingService
{
    /// <summary>
    /// Resolves a free-text location string to geographic coordinates.
    /// </summary>
    /// <param name="locationText">The free-text location to geocode (e.g. "Paris, France").</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The geocoding result, or <c>null</c> if the location cannot be resolved or an error occurs.</returns>
    Task<GeocodingResult?> GeocodeAsync(string locationText, CancellationToken cancellationToken = default);
}
