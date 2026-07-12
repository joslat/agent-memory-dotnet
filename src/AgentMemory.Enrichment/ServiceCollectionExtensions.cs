using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Enrichment.Http;

namespace AgentMemory.Enrichment;

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
        Action<WikimediaEnrichmentOptions>? configureEnrichment = null,
        Action<EnrichmentCacheOptions>? configureCaching = null)
    {
        // Options
        var geoOptions = services.AddOptions<GeocodingOptions>();
        if (configureGeocoding is not null)
            geoOptions.Configure(configureGeocoding);
        geoOptions
            .Validate(o => !string.IsNullOrWhiteSpace(o.BaseUrl), "Geocoding BaseUrl must be provided.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.UserAgent), "Geocoding UserAgent must be provided (Nominatim requires it).")
            .Validate(o => o.TimeoutSeconds > 0, "Geocoding TimeoutSeconds must be positive.")
            .Validate(o => o.RateLimitPerSecond > 0, "Geocoding RateLimitPerSecond must be positive.")
            .Validate(o => o.MaxRetries >= 0, "Geocoding MaxRetries must be non-negative.")
            .ValidateOnStart();

        var enrichOptions = services.AddOptions<WikimediaEnrichmentOptions>();
        if (configureEnrichment is not null)
            enrichOptions.Configure(configureEnrichment);
        enrichOptions
            .Validate(o => !string.IsNullOrWhiteSpace(o.WikipediaLanguage), "Enrichment WikipediaLanguage must be provided.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.WikipediaBaseUrl), "Enrichment WikipediaBaseUrl must be provided.")
            .Validate(o => o.TimeoutSeconds > 0, "Enrichment TimeoutSeconds must be positive.")
            .Validate(o => o.MaxRetries >= 0, "Enrichment MaxRetries must be non-negative.")
            .ValidateOnStart();

        var cacheOptions = services.AddOptions<EnrichmentCacheOptions>();
        if (configureCaching is not null)
            cacheOptions.Configure(configureCaching);

        // In-memory cache (no-op if already registered)
        services.AddMemoryCache();

        // Named HTTP clients. Each gets a transient-retry handler honoring its MaxRetries option.
        services.AddHttpClient(NominatimGeocodingService.ClientName, (sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<GeocodingOptions>>().Value;
            client.DefaultRequestHeaders.UserAgent.ParseAdd(opts.UserAgent);
            client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
        })
        .AddHttpMessageHandler(sp => new RetryHttpMessageHandler(
            sp.GetRequiredService<IOptions<GeocodingOptions>>().Value.MaxRetries,
            sp.GetService<ILogger<RetryHttpMessageHandler>>()));

        services.AddHttpClient(WikimediaEnrichmentService.ClientName, (sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<WikimediaEnrichmentOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AgentMemory/1.0");
        })
        .AddHttpMessageHandler(sp => new RetryHttpMessageHandler(
            sp.GetRequiredService<IOptions<WikimediaEnrichmentOptions>>().Value.MaxRetries,
            sp.GetService<ILogger<RetryHttpMessageHandler>>()));

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

    /// <summary>
    /// Registers <see cref="DiffbotEnrichmentService"/> as a typed HTTP client.
    /// The service is available via <see cref="DiffbotEnrichmentService"/> directly.
    /// </summary>
    public static IServiceCollection AddDiffbotEnrichment(
        this IServiceCollection services,
        Action<DiffbotEnrichmentOptions> configure)
    {
        services.AddOptions<DiffbotEnrichmentOptions>()
            .Configure(configure)
            .Validate(o => !string.IsNullOrWhiteSpace(o.ApiKey), "Diffbot ApiKey must be provided.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.BaseUrl), "Diffbot BaseUrl must be provided.")
            .Validate(o => o.RateLimitSeconds >= 0, "Diffbot RateLimitSeconds must be non-negative.")
            .Validate(o => o.Timeout > TimeSpan.Zero, "Diffbot Timeout must be positive.")
            .ValidateOnStart();

        services.AddHttpClient<DiffbotEnrichmentService>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<DiffbotEnrichmentOptions>>().Value;
            client.Timeout = opts.Timeout;
        });

        return services;
    }
}
