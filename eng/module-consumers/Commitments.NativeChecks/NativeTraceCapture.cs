using System.Collections.Concurrent;
using System.Diagnostics;
using AgentMemory.Abstractions.Diagnostics;

namespace Commitments.NativeChecks;

// Fixture-only observation. The library still owns exactly one diagnostics source and no exporter.
internal sealed class NativeTraceCapture : IDisposable
{
    private readonly Activity _root = new Activity("native.acceptance").SetIdFormat(ActivityIdFormat.W3C).Start();
    private readonly ConcurrentQueue<object> _operations = new();
    private readonly ConcurrentQueue<object> _spans = new();
    private readonly ActivityListener _listener;
    public NativeTraceCapture()
    {
        // Materialize the source before subscribing. During its own constructor the static Source
        // field is still null, so comparing against that field in ShouldListenTo loses a cold source.
        var source = AgentMemoryDiagnostics.Source;
        _listener = new ActivityListener
        {
            ShouldListenTo = candidate => ReferenceEquals(candidate, source),
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => options.Parent.TraceId == _root.TraceId
                ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.None,
            ActivityStopped = activity =>
            {
                if (activity.TraceId != _root.TraceId) return;
                _spans.Enqueue(new
                {
                    name = activity.OperationName, source = activity.Source.Name,
                    traceId = activity.TraceId.ToString(), spanId = activity.SpanId.ToString(),
                    parentSpanId = activity.ParentSpanId.ToString(), status = activity.Status.ToString(),
                    statusDescription = activity.StatusDescription,
                    startedAtUtc = activity.StartTimeUtc, durationMs = activity.Duration.TotalMilliseconds,
                    tags = activity.TagObjects.ToDictionary(t => t.Key, t => t.Value),
                    events = activity.Events.Select(e => new { name = e.Name, tags = e.Tags.ToDictionary(t => t.Key, t => t.Value) }).ToArray()
                });
            }
        };
        ActivitySource.AddActivityListener(_listener);
    }
    public Activity BeginOperation(string name)
    {
        var activity = new Activity(name).Start();
        _operations.Enqueue(new { name, spanId = activity.SpanId.ToString(), parentSpanId = activity.ParentSpanId.ToString() });
        return activity;
    }
    public object Snapshot() => new { rootSpanId = _root.SpanId.ToString(), traceId = _root.TraceId.ToString(),
        operations = _operations.ToArray(), spans = _spans.ToArray() };
    public void Dispose() { _listener.Dispose(); _root.Dispose(); }
}
