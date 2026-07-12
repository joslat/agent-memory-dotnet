using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain.Enrichment;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Enrichment;

/// <summary>
/// Caching decorator for <see cref="IEnrichmentService"/> using in-memory cache.
/// </summary>
internal sealed class CachedEnrichmentService : IEnrichmentService
{
    private readonly IEnrichmentService _inner;
    private readonly IMemoryCache _cache;
    private readonly EnrichmentCacheOptions _options;
    private readonly ILogger<CachedEnrichmentService> _logger;

    public CachedEnrichmentService(
        IEnrichmentService inner,
        IMemoryCache cache,
        IOptions<EnrichmentCacheOptions> options,
        ILogger<CachedEnrichmentService> logger)
    {
        _inner = inner;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<EnrichmentResult?> EnrichEntityAsync(
        string entityName,
        string entityType,
        CancellationToken ct = default)
    {
        var key = $"enrichment:{_inner.GetType().Name}:{entityName.Trim().ToLowerInvariant()}:{entityType.Trim().ToLowerInvariant()}";

        if (_cache.TryGetValue(key, out EnrichmentResult? cached))
        {
            _logger.LogDebug("Cache hit for enrichment key '{Key}'", key);
            return cached;
        }

        var result = await _inner.EnrichEntityAsync(entityName, entityType, ct).ConfigureAwait(false);

        if (result is not null && IsCacheable(result))
        {
            _cache.Set(key, result, _options.EnrichmentCacheDuration);
            _logger.LogDebug("Cached enrichment result for key '{Key}'", key);
        }

        return result;
    }

    // Providers signal TRANSIENT failures (HTTP 429/5xx) by RETURNING a non-null result with
    // Status=RateLimited/Error (not by throwing); BackgroundEnrichmentQueue retries exactly those. Caching a
    // transient failure for EnrichmentCacheDuration (default 24h) would make that retry short-circuit on the
    // cached poison result without re-contacting the provider — the entity is never enriched until the TTL
    // expires. Cache only non-transient outcomes: Success, null-status (Wikimedia success), and terminal
    // NotFound/Skipped (a genuinely absent/unsupported entity is worth caching). The result is still RETURNED
    // unchanged so the queue keeps observing the Error/RateLimited status to drive its retry.
    private static bool IsCacheable(EnrichmentResult result) =>
        result.Status is not (EnrichmentStatus.Error or EnrichmentStatus.RateLimited);
}
