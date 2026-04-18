using System.Diagnostics;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Observability;

/// <summary>
/// Decorator that wraps <see cref="IFactExtractor"/> with OpenTelemetry tracing and metrics.
/// </summary>
internal sealed class InstrumentedFactExtractor : IFactExtractor
{
    private readonly IFactExtractor _inner;
    private readonly MemoryMetrics _metrics;

    public InstrumentedFactExtractor(IFactExtractor inner, MemoryMetrics metrics)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    }

    public async Task<IReadOnlyList<ExtractedFact>> ExtractAsync(
        IReadOnlyList<Message> messages,
        CancellationToken cancellationToken = default)
    {
        using var activity = MemoryActivitySource.Instance.StartActivity("memory.extraction.facts");
        activity?.SetTag("memory.extraction.message_count", messages.Count);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _inner.ExtractAsync(messages, cancellationToken);
            _metrics.FactsExtracted.Add(result.Count);
            activity?.SetTag("memory.extraction.fact_count", result.Count);
            return result;
        }
        catch (Exception ex)
        {
            _metrics.ExtractionErrors.Add(1);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            _metrics.FactExtractionDurationMs.Record(sw.Elapsed.TotalMilliseconds);
        }
    }
}
