using System.Diagnostics;
using Neo4j.AgentMemory.Abstractions.Domain.Enrichment;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Observability;

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
            var result = await _inner.EnrichEntityAsync(entityName, entityType, ct);
            _metrics.EnrichmentRequests.Add(1);
            activity?.SetTag("memory.enrichment.enriched", result is not null);
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
