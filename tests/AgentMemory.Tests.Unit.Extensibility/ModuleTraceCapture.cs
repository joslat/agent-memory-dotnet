using System.Diagnostics;
using AgentMemory.Abstractions.Diagnostics;

namespace AgentMemory.Tests.Unit.Extensibility;

internal sealed class ModuleTraceCapture : IDisposable
{
    private readonly Activity _root = new Activity("module-ingestion-telemetry-test").Start();
    private readonly ActivityListener _listener;
    public List<Activity> Stopped { get; } = [];
    public ActivitySpanId ParentSpanId => _root.SpanId;
    public ModuleTraceCapture(ActivitySamplingResult sample = ActivitySamplingResult.AllDataAndRecorded)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentMemoryDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => options.Parent.TraceId == _root.TraceId
                ? sample : ActivitySamplingResult.None,
            ActivityStopped = activity => { if (activity.TraceId == _root.TraceId) Stopped.Add(activity); }
        };
        ActivitySource.AddActivityListener(_listener);
    }
    public void Dispose() { _listener.Dispose(); _root.Dispose(); }
}

