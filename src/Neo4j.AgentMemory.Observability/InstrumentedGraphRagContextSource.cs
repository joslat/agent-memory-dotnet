using System.Diagnostics;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Observability;

/// <summary>
/// Decorator that wraps <see cref="IGraphRagContextSource"/> with OpenTelemetry tracing and metrics.
/// </summary>
internal sealed class InstrumentedGraphRagContextSource : IGraphRagContextSource
{
    private readonly IGraphRagContextSource _inner;
    private readonly MemoryMetrics _metrics;

    public InstrumentedGraphRagContextSource(IGraphRagContextSource inner, MemoryMetrics metrics)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    }

    public async Task<GraphRagContextResult> GetContextAsync(
        GraphRagContextRequest request,
        CancellationToken cancellationToken = default)
    {
        using var activity = MemoryActivitySource.Instance.StartActivity("memory.graphrag.query");
        activity?.SetTag("memory.graphrag.search_mode", request.SearchMode.ToString());
        activity?.SetTag("memory.graphrag.top_k", request.TopK);
        activity?.SetTag("memory.session_id", request.SessionId);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _inner.GetContextAsync(request, cancellationToken);
            _metrics.GraphRagQueries.Add(1);
            activity?.SetTag("memory.graphrag.result_count", result.Items.Count);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            _metrics.GraphRagDurationMs.Record(sw.Elapsed.TotalMilliseconds);
        }
    }
}
