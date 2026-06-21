using System.Diagnostics;
using AgentMemory.Abstractions.Domain.Enrichment;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Observability;

/// <summary>
/// Decorator that wraps <see cref="IEnrichmentService"/> with OpenTelemetry tracing and metrics.
/// </summary>
internal sealed class InstrumentedEnrichmentService : IEnrichmentService
{
    private readonly IEnrichmentService _inner;
    private readonly MemoryMetrics _metrics;

    public InstrumentedEnrichmentService(IEnrichmentService inner, MemoryMetrics metrics)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    }

    public async Task<EnrichmentResult?> EnrichEntityAsync(
        string entityName,
        string entityType,
        CancellationToken ct = default)
    {
        using var activity = MemoryActivitySource.Instance.StartActivity("memory.enrichment");
        activity?.SetTag("memory.enrichment.entity_name", entityName);
        activity?.SetTag("memory.enrichment.entity_type", entityType);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _inner.EnrichEntityAsync(entityName, entityType, ct).ConfigureAwait(false);
            _metrics.EnrichmentRequests.Add(1);

            // Providers (e.g. Diffbot) return a status-bearing result instead of throwing, so a non-null
            // Error/RateLimited result is a FAILURE, not an enrichment. Branch on Status — counting it as
            // success would hide transient outages from telemetry (R6 cleanup; mirrors the
            // BackgroundEnrichmentQueue / CachedEnrichmentService status-aware fixes).
            bool failed = result is null
                || result.Status is EnrichmentStatus.Error or EnrichmentStatus.RateLimited;
            bool enriched = !failed && result is not null;
            activity?.SetTag("memory.enrichment.enriched", enriched);
            if (result?.Status is EnrichmentStatus.Error or EnrichmentStatus.RateLimited)
            {
                _metrics.EnrichmentErrors.Add(1);
                activity?.SetTag("memory.enrichment.status", result.Status.ToString());
            }
            return result;
        }
        catch (Exception ex)
        {
            _metrics.EnrichmentErrors.Add(1);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            _metrics.EnrichmentDurationMs.Record(sw.Elapsed.TotalMilliseconds);
        }
    }
}
