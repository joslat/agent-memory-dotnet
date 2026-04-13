using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Enrichment;

/// <summary>
/// Extension methods for registering enrichment services with the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers geocoding and entity enrichment services.
    /// Decorators are applied in order: rate limiter wraps the raw service, cache wraps the rate limiter.
    /// </summary>
    public static IServiceCollection AddEnrichmentServices(
        this IServiceCollection services,
        Action<GeocodingOptions>? configureGeocoding = null,
        Action<EnrichmentOptions>? configureEnrichment = null,
        Action<EnrichmentCacheOptions>? configureCaching = null)
    {
        // Options
        var geoOptions = services.AddOptions<GeocodingOptions>();
        if (configureGeocoding is not null)
            geoOptions.Configure(configureGeocoding);

        var enrichOptions = services.AddOptions<EnrichmentOptions>();
        if (configureEnrichment is not null)
            enrichOptions.Configure(configureEnrichment);

        var cacheOptions = services.AddOptions<EnrichmentCacheOptions>();
        if (configureCaching is not null)
            cacheOptions.Configure(configureCaching);

        // In-memory cache (no-op if already registered)
        services.AddMemoryCache();

        // Named HTTP clients
        services.AddHttpClient(NominatimGeocodingService.ClientName, (sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<GeocodingOptions>>().Value;
            client.DefaultRequestHeaders.UserAgent.ParseAdd(opts.UserAgent);
            client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
        });

        services.AddHttpClient(WikimediaEnrichmentService.ClientName, (sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<EnrichmentOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Neo4j.AgentMemory/1.0");
        });

        // Register concrete implementations for direct resolution
        services.TryAddSingleton<NominatimGeocodingService>();
        services.TryAddSingleton<WikimediaEnrichmentService>();

        // Geocoding decorator chain: Cache → RateLimiter → Nominatim
        services.TryAddSingleton<RateLimitedGeocodingService>(sp => new RateLimitedGeocodingService(
            sp.GetRequiredService<NominatimGeocodingService>(),
            sp.GetRequiredService<IOptions<GeocodingOptions>>(),
            sp.GetRequiredService<ILogger<RateLimitedGeocodingService>>()));

        services.TryAddSingleton<IGeocodingService>(sp => new CachedGeocodingService(
            sp.GetRequiredService<RateLimitedGeocodingService>(),
            sp.GetRequiredService<IMemoryCache>(),
            sp.GetRequiredService<IOptions<EnrichmentCacheOptions>>(),
            sp.GetRequiredService<ILogger<CachedGeocodingService>>()));

        // Enrichment decorator chain: Cache → Wikimedia
        services.TryAddSingleton<IEnrichmentService>(sp => new CachedEnrichmentService(
            sp.GetRequiredService<WikimediaEnrichmentService>(),
            sp.GetRequiredService<IMemoryCache>(),
            sp.GetRequiredService<IOptions<EnrichmentCacheOptions>>(),
            sp.GetRequiredService<ILogger<CachedEnrichmentService>>()));

        return services;
    }
}
