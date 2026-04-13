using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain.Enrichment;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Enrichment;

/// <summary>
/// Caching decorator for <see cref="IGeocodingService"/> using in-memory cache.
/// </summary>
public sealed class CachedGeocodingService : IGeocodingService
{
    private readonly IGeocodingService _inner;
    private readonly IMemoryCache _cache;
    private readonly EnrichmentCacheOptions _options;
    private readonly ILogger<CachedGeocodingService> _logger;

    public CachedGeocodingService(
        IGeocodingService inner,
        IMemoryCache cache,
        IOptions<EnrichmentCacheOptions> options,
        ILogger<CachedGeocodingService> logger)
    {
        _inner = inner;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<GeocodingResult?> GeocodeAsync(string locationText, CancellationToken ct = default)
    {
        var key = $"geocoding:{locationText.Trim().ToLowerInvariant()}";

        if (_cache.TryGetValue(key, out GeocodingResult? cached))
        {
            _logger.LogDebug("Cache hit for geocoding key '{Key}'", key);
            return cached;
        }

        var result = await _inner.GeocodeAsync(locationText, ct).ConfigureAwait(false);

        if (result is not null)
        {
            _cache.Set(key, result, _options.GeocodingCacheDuration);
            _logger.LogDebug("Cached geocoding result for key '{Key}'", key);
        }

        return result;
    }
}
