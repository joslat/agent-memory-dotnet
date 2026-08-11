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

/// <summary>
/// Aggregate vector-recall yield for one arm: how much of the requested width the owner received.
/// </summary>
/// <remarks>
/// <c>StarvedSearches</c> is the number that returned <b>nothing</b> while the search itself succeeded
/// — the shape that produced a question with no facts at all on the 50-owner corpus. It is counted
/// separately from the mean because an average hides it completely.
/// <para>
/// <b><c>MeanFillRatio</c> is the starvation measure; <c>MeanYieldRatio</c> is not.</b> The first
/// version of this summary reported only <c>returned / effective_topk</c>, and the first live run made
/// it read as severe starvation — 10 of 60, 0.167 — when the search had in fact returned <i>everything
/// it asked for</i>. <c>returned</c> is capped by the Cypher <c>LIMIT $limit</c>, so dividing by the
/// over-fetch width conflates "how much the owner received" with "how much wider the probe was than
/// the request". <c>returned / limit</c> is the fraction of the request that survived the owner
/// post-filter, and 1.0 means nothing was lost. <c>MeanYieldRatio</c> is kept because it still says
/// how much of the over-fetch was consumed, which is what sizing the over-fetch needs.
/// </para>
/// </remarks>
internal sealed record LongMemEvalVectorYieldSummary(
    int Searches,
    int OwnerScopedSearches,
    int StarvedSearches,
    int EscalatedSearches,
    double MeanReturned,
    double MeanFillRatio,
    double MeanYieldRatio,
    int TotalReturned,
    IReadOnlyDictionary<string, int> SearchesBySpan)
{
    internal static LongMemEvalVectorYieldSummary From(IReadOnlyList<VectorYieldSample> samples)
    {
        if (samples.Count == 0)
        {
            return new LongMemEvalVectorYieldSummary(
                0, 0, 0, 0, 0, 0, 0, 0, new Dictionary<string, int>(StringComparer.Ordinal));
        }

        // Ratio against effective_topk, the width that actually produced the rows - not the requested
        // width, which on an escalated search is not what the second query asked for.
        var ratios = samples
            .Where(sample => sample.EffectiveTopK > 0)
            .Select(sample => (double)sample.Returned / sample.EffectiveTopK)
            .ToArray();

        var fills = samples
            .Where(sample => sample.Limit > 0)
            .Select(sample => Math.Min(1.0, (double)sample.Returned / sample.Limit))
            .ToArray();

        return new LongMemEvalVectorYieldSummary(
            samples.Count,
            samples.Count(sample => sample.OwnerScoped),
            samples.Count(sample => sample.Returned == 0),
            samples.Count(sample => sample.Escalated),
            samples.Average(sample => sample.Returned),
            fills.Length == 0 ? 0 : fills.Average(),
            ratios.Length == 0 ? 0 : ratios.Average(),
            samples.Sum(sample => sample.Returned),
            samples.GroupBy(sample => sample.Span, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal));
    }
}
