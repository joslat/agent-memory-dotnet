using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain.Enrichment;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Enrichment;

/// <summary>
/// Caching decorator for <see cref="IEnrichmentService"/> using in-memory cache.
/// </summary>
public sealed class CachedEnrichmentService : IEnrichmentService
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
        var key = $"enrichment:{entityName.Trim().ToLowerInvariant()}:{entityType.Trim().ToLowerInvariant()}";

        if (_cache.TryGetValue(key, out EnrichmentResult? cached))
        {
            _logger.LogDebug("Cache hit for enrichment key '{Key}'", key);
            return cached;
        }

        var result = await _inner.EnrichEntityAsync(entityName, entityType, ct).ConfigureAwait(false);

        if (result is not null)
        {
            _cache.Set(key, result, _options.EnrichmentCacheDuration);
            _logger.LogDebug("Cached enrichment result for key '{Key}'", key);
        }

        return result;
    }
}
