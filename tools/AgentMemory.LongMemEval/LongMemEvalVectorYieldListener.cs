using System.Collections.Concurrent;
using System.Diagnostics;
using AgentMemory.Abstractions.Diagnostics;

namespace AgentMemory.LongMemEval;

/// <summary>
/// Collects the vector-recall yield telemetry that the repositories emit during an evaluation run.
/// </summary>
/// <remarks>
/// <para>
/// Eight vector searches were instrumented with <c>owner_scoped</c>, <c>limit</c>,
/// <c>requested_topk</c>, <c>effective_topk</c>, <c>escalated</c> and <c>returned</c> — and <b>nothing
/// in this harness listened</b>, so every one of those spans was created and dropped on every run.
/// Emitting is not observing; this is the consumer that makes the instrumentation real.
/// </para>
/// <para>
/// What it exists to quantify: owner-scoped vector search post-filters a <i>global</i> top-K, so the
/// querying owner receives only the rows that survive the filter — a mean of 7 of 60 candidates on a
/// 50-owner corpus, with at least one question receiving none. Until now that was measured once, by
/// hand, on one path.
/// </para>
/// <para>
/// <b>Sampling is name-filtered on purpose.</b> All AgentMemory spans share one
/// <see cref="ActivitySource"/>, so a listener that samples every name forces an <see cref="Activity"/>
/// into existence at every other call site in the process. In the unit suite that turned unrelated
/// telemetry tests flaky; here it would add allocation to every span an evaluation run touches, for
/// data nobody reads.
/// </para>
/// </remarks>
internal sealed class LongMemEvalVectorYieldListener : IDisposable
{
    private const string TagPrefix = "memory.vector.";

    private static readonly HashSet<string> VectorRecallSpans = new(StringComparer.Ordinal)
    {
        "memory.recall.fact_vector",
        "memory.recall.fact_vector_as_of",
        "memory.recall.entity_vector",
        "memory.recall.entity_vector_as_of",
        "memory.recall.entity_similar_vector",
        "memory.recall.preference_vector",
        "memory.recall.preference_vector_as_of",
        "memory.recall.trace_vector",
        "memory.recall.trace_vector_as_of",
    };

    private readonly ActivityListener _listener;
    private readonly ConcurrentBag<VectorYieldSample> _samples = [];

    public LongMemEvalVectorYieldListener()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentMemoryDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                VectorRecallSpans.Contains(options.Name)
                    ? ActivitySamplingResult.AllDataAndRecorded
                    : ActivitySamplingResult.None,
            ActivityStopped = OnStopped,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public IReadOnlyList<VectorYieldSample> Samples => _samples.ToArray();

    private void OnStopped(Activity activity)
    {
        if (!VectorRecallSpans.Contains(activity.OperationName)) return;

        // A search that FAILED publishes no yield tags at all, deliberately: tagging it returned = 0
        // would be indistinguishable from genuine total starvation. Absent means "not measured", so
        // such a span is skipped rather than recorded as a zero.
        var returned = TagAsInt(activity, "returned");
        if (returned is null) return;

        _samples.Add(new VectorYieldSample(
            activity.OperationName,
            TagAsBool(activity, "owner_scoped") ?? false,
            TagAsInt(activity, "limit") ?? 0,
            TagAsInt(activity, "requested_topk") ?? 0,
            TagAsInt(activity, "effective_topk") ?? 0,
            TagAsBool(activity, "escalated") ?? false,
            returned.Value));
    }

    private static int? TagAsInt(Activity activity, string name) =>
        activity.GetTagItem(TagPrefix + name) is int value ? value : null;

    private static bool? TagAsBool(Activity activity, string name) =>
        activity.GetTagItem(TagPrefix + name) is bool value ? value : null;

    public void Dispose() => _listener.Dispose();
}

/// <summary>One completed vector search: what it asked for, and what the owner actually received.</summary>
internal sealed record VectorYieldSample(
    string Span,
    bool OwnerScoped,
    int Limit,
    int RequestedTopK,
    int EffectiveTopK,
    bool Escalated,
    int Returned);
