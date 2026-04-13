using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain.Enrichment;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Enrichment;

/// <summary>
/// Rate-limiting decorator for <see cref="IGeocodingService"/>.
/// Ensures at most N requests per second are forwarded to the inner service.
/// </summary>
public sealed class RateLimitedGeocodingService : IGeocodingService, IDisposable
{
    private readonly IGeocodingService _inner;
    private readonly int _rateLimitPerSecond;
    private readonly ILogger<RateLimitedGeocodingService> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private DateTimeOffset _lastRequestTime = DateTimeOffset.MinValue;

    public RateLimitedGeocodingService(
        IGeocodingService inner,
        IOptions<GeocodingOptions> options,
        ILogger<RateLimitedGeocodingService> logger)
    {
        _inner = inner;
        _rateLimitPerSecond = Math.Max(1, options.Value.RateLimitPerSecond);
        _logger = logger;
    }

    public async Task<GeocodingResult?> GeocodeAsync(string locationText, CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var minInterval = TimeSpan.FromSeconds(1.0 / _rateLimitPerSecond);
            var elapsed = DateTimeOffset.UtcNow - _lastRequestTime;

            if (elapsed < minInterval)
            {
                var delay = minInterval - elapsed;
                _logger.LogDebug("Rate limiter delaying geocoding request by {DelayMs}ms", delay.TotalMilliseconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }

            _lastRequestTime = DateTimeOffset.UtcNow;
            return await _inner.GeocodeAsync(locationText, ct).ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void Dispose() => _semaphore.Dispose();
}
