using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Unit.TestHelpers;
using Neo4j.Driver;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AgentMemory.Tests.Unit.Repositories;

/// <summary>
/// The owner-scoped reasoning-trace vector searches — live and as-of — must report their own recall
/// yield, and additionally whether the outcome filter (<c>RecallOptions.SuccessfulTracesOnly</c>) was
/// narrowing the candidates while they did it.
/// </summary>
/// <remarks>
/// A yield number means something different when an outcome filter is in play: with
/// <c>node.success = $successFilter</c> in the WHERE clause, the same global top-K is cut twice, and a
/// consumer reading "2 of 60" cannot tell owner starvation from an outcome filter doing its job. Worse,
/// in Cypher <c>null = true</c> is null, so a successful-only recall silently drops every trace whose
/// outcome was never recorded — a real, already-observed failure on this path.
/// <para>
/// Width assertions cross-check the tag against the width baked into the Cypher that was actually issued
/// (<c>db.index.vector.queryNodes('task_embedding_idx', &lt;topK&gt;, …)</c>).
/// </para>
/// </remarks>
public sealed class TraceVectorYieldTelemetryTests
{
    private const string LiveSpan = "memory.recall.trace_vector";
    private const string AsOfSpan = "memory.recall.trace_vector_as_of";
    private static readonly float[] Query = [0.1f, 0.2f, 0.3f];
    private static readonly DateTimeOffset AsOf = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AScopedLiveSearchReportsTheWidthItAskedForAndTheRowsItGotBack()
    {
        var (repo, cyphers) = CreateRepository(rowsPerQuery: 3);
        using var listening = Listen();

        var results = await repo.SearchByTaskVectorAsync(
            Query, successFilter: null, limit: 10, scope: MemoryScope.For("owner-a"));

        results.Should().HaveCount(3);
        cyphers.Should().ContainSingle()
            .Which.Should().Contain("db.index.vector.queryNodes('task_embedding_idx', 60,");

        var span = listening.Single(LiveSpan);
        span.GetTagItem("memory.vector.owner_scoped").Should().Be(true);
        span.GetTagItem("memory.vector.limit").Should().Be(10);
        span.GetTagItem("memory.vector.requested_topk").Should().Be(60);
        span.GetTagItem("memory.vector.effective_topk").Should().Be(60);
        span.GetTagItem("memory.vector.returned").Should().Be(3);
    }

    [Fact]
    public async Task ASearchWithNoOutcomeFilterSaysSoAndCarriesNoFilterValue()
    {
        // Unfiltered is the default. The flag is emitted on every trace search so a consumer never has to
        // infer "no filter" from a missing tag; the value is absent because there was no value to report.
        var (repo, cyphers) = CreateRepository(rowsPerQuery: 5);
        using var listening = Listen();

        await repo.SearchByTaskVectorAsync(
            Query, successFilter: null, limit: 10, scope: MemoryScope.For("owner-a"));

        cyphers.Should().ContainSingle().Which.Should().NotContain("node.success = $successFilter");

        var span = listening.Single(LiveSpan);
        span.GetTagItem("memory.vector.success_filtered").Should().Be(false);
        span.GetTagItem("memory.vector.success_filter").Should().BeNull();
    }

    [Fact]
    public async Task ASuccessfulOnlySearchReportsThatAnOutcomeFilterCutTheCandidates()
    {
        // 2 of 60 with an outcome filter applied is a different reading from 2 of 60 without one, and the
        // yield alone cannot distinguish them.
        var (repo, cyphers) = CreateRepository(rowsPerQuery: 2);
        using var listening = Listen();

        await repo.SearchByTaskVectorAsync(
            Query, successFilter: true, limit: 10, scope: MemoryScope.For("owner-a"));

        cyphers.Should().ContainSingle().Which.Should().Contain("node.success = $successFilter");

        var span = listening.Single(LiveSpan);
        span.GetTagItem("memory.vector.success_filtered").Should().Be(true);
        span.GetTagItem("memory.vector.success_filter").Should().Be(true);
        span.GetTagItem("memory.vector.returned").Should().Be(2);
    }

    [Fact]
    public async Task AFailedOnlySearchIsReportedAsFilteredWithItsPolarity()
    {
        // The filter is a three-state input (null / true / false), so "filtered" and "which way" are two
        // separate facts. Encoding the polarity alone would make a failures-only search indistinguishable
        // from an unfiltered one to anything reading a bool.
        var (repo, _) = CreateRepository(rowsPerQuery: 6);
        using var listening = Listen();

        await repo.SearchByTaskVectorAsync(
            Query, successFilter: false, limit: 10, scope: MemoryScope.For("owner-a"));

        var span = listening.Single(LiveSpan);
        span.GetTagItem("memory.vector.success_filtered").Should().Be(true);
        span.GetTagItem("memory.vector.success_filter").Should().Be(false);
    }

    [Fact]
    public async Task AnUnscopedLiveSearchIsMarkedUnscopedAndAsksOnlyForTheLimit()
    {
        var (repo, cyphers) = CreateRepository(rowsPerQuery: 4);
        using var listening = Listen();

        await repo.SearchByTaskVectorAsync(Query, successFilter: null, limit: 10, scope: null);

        cyphers.Should().ContainSingle()
            .Which.Should().Contain("db.index.vector.queryNodes('task_embedding_idx', 10,");

        var span = listening.Single(LiveSpan);
        span.GetTagItem("memory.vector.owner_scoped").Should().Be(false);
        span.GetTagItem("memory.vector.requested_topk").Should().Be(10);
        span.GetTagItem("memory.vector.effective_topk").Should().Be(10);
        span.GetTagItem("memory.vector.returned").Should().Be(4);
    }

    [Fact]
    public async Task AScopedAsOfSearchReportsItsOwnYieldAndFilterUnderItsOwnSpanName()
    {
        var (repo, cyphers) = CreateRepository(rowsPerQuery: 1);
        using var listening = Listen();

        var results = await repo.SearchByTaskVectorAsOfAsync(
            Query, AsOf, successFilter: true, limit: 10, scope: MemoryScope.For("owner-a"));

        results.Should().HaveCount(1);
        cyphers.Should().ContainSingle()
            .Which.Should().Contain("db.index.vector.queryNodes('task_embedding_idx', 60,");

        var span = listening.Single(AsOfSpan);
        span.GetTagItem("memory.vector.owner_scoped").Should().Be(true);
        span.GetTagItem("memory.vector.requested_topk").Should().Be(60);
        span.GetTagItem("memory.vector.effective_topk").Should().Be(60);
        span.GetTagItem("memory.vector.returned").Should().Be(1);
        span.GetTagItem("memory.vector.success_filtered").Should().Be(true);
        span.GetTagItem("memory.vector.success_filter").Should().Be(true);
    }

    [Fact]
    public async Task NeitherTracePathClaimsAnEscalationItDoesNotHave()
    {
        // NARROWED, not weakened. The LIVE trace search now retries an empty scoped result, because the
        // same query shape was measured returning 0 of an owner's 4 rows against 500 more-similar
        // foreign rows until the retry was added. The AS-OF path still issues exactly one query, so the
        // invariant holds for it unchanged and is what this test now covers.
        var (repo, cyphers) = CreateRepository(rowsPerQuery: [0, 0]);
        using var listening = Listen();

        await repo.SearchByTaskVectorAsOfAsync(
            Query, AsOf, successFilter: null, limit: 10, scope: MemoryScope.For("owner-a"));

        cyphers.Should().HaveCount(1, because: "the as-of path has no rescue, so one query and no more");
        foreach (var span in listening.Spans)
        {
            span.GetTagItem("memory.vector.returned").Should().Be(0);
            span.GetTagItem("memory.vector.escalated").Should().Be(false);
            span.GetTagItem("memory.vector.escalated_topk").Should().BeNull();
        }
    }


    [Fact]
    public async Task TheLiveTraceSearchNowReportsTheEscalationItPerforms()
    {
        var (repo, cyphers) = CreateRepository(rowsPerQuery: [0, 0]);
        using var listening = Listen();

        await repo.SearchByTaskVectorAsync(
            Query, successFilter: null, limit: 10, scope: MemoryScope.For("owner-a"));

        // Indexed pass, one widened retry, then the owner-scoped similarity scan. Bounded at three.
        cyphers.Should().HaveCount(3, because: "indexed, widened, then the scoped fallback - no more");
        var span = listening.Spans.Single();
        span.GetTagItem("memory.vector.escalated").Should().Be(true);
        span.GetTagItem("memory.vector.escalated_topk").Should().NotBeNull();
    }

    [Fact]
    public async Task AFilteredTraceSearchStillEscalatesRatherThanGuessingWhyItWasEmpty()
    {
        // A successFilter search can be empty because nothing MATCHED, not because of crowding. The
        // retry is issued either way: it costs one wider query and cannot invent a match, whereas
        // skipping it would require guessing which cause applied and would silently reinstate the
        // starvation on every filtered search.
        var (repo, cyphers) = CreateRepository(rowsPerQuery: [0, 0]);
        using var listening = Listen();

        await repo.SearchByTaskVectorAsync(
            Query, successFilter: true, limit: 10, scope: MemoryScope.For("owner-a"));

        // The filtered search escalates AND falls back, and the fallback carries the success filter
        // through - so a filtered search that genuinely matches nothing still returns nothing rather
        // than being rescued into the wrong answer.
        cyphers.Should().HaveCount(3);
        listening.Spans.Single().GetTagItem("memory.vector.escalated").Should().Be(true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASearchThatNeverReachedTheIndexReportsNoYieldAtAll(bool asOf)
    {
        // A degraded (zero-dimension) task embedding short-circuits before any query is issued. Emitting a
        // zero-yield reading for a search that never ran would manufacture starvation that did not happen.
        var (repo, cyphers) = CreateRepository(rowsPerQuery: 5);
        using var listening = Listen();

        var results = asOf
            ? await repo.SearchByTaskVectorAsOfAsync(
                [], AsOf, successFilter: null, limit: 10, scope: MemoryScope.For("owner-a"))
            : await repo.SearchByTaskVectorAsync(
                [], successFilter: null, limit: 10, scope: MemoryScope.For("owner-a"));

        results.Should().BeEmpty();
        cyphers.Should().BeEmpty();
        listening.Spans.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedSearchReportsNoYieldRatherThanAFalseZero()
    {
        // A query that threw measured nothing. Tagging it `returned = 0` would put an indistinguishable
        // row next to a genuine total-starvation reading, which is the one outcome worth avoiding most.
        var tx = Substitute.For<INeo4jTransactionRunner>();
        tx.ReadAsync(
                Arg.Any<Func<IAsyncQueryRunner, Task<List<(ReasoningTrace, double)>>>>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new ServiceUnavailableException("index offline"));
        var repo = new Neo4jReasoningTraceRepository(tx, NullLogger<Neo4jReasoningTraceRepository>.Instance);
        using var listening = Listen();

        var search = () => repo.SearchByTaskVectorAsync(
            Query, successFilter: true, limit: 10, scope: MemoryScope.For("owner-a"));
        await search.Should().ThrowAsync<ServiceUnavailableException>();

        var span = listening.Single(LiveSpan);
        span.GetTagItem("memory.vector.returned").Should().BeNull();
    }

    [Fact]
    public async Task NothingIsMeasuredWhenNoListenerWantsTheData()
    {
        // Zero overhead: with sampling declined the Activity is never created, so no tag value is
        // computed and the search behaves exactly as it does with no diagnostics at all.
        var (repo, cyphers) = CreateRepository(rowsPerQuery: 4);
        using var listening = Listen(ActivitySamplingResult.None);

        // Sampling is a process-wide decision — the result is the MAX over every attached listener — and
        // xUnit runs test classes in parallel. A neighbouring class whose listener samples everything
        // creates this span no matter what this listener says, so probe for that first; otherwise this
        // test measures its neighbours rather than the repository.
        bool sampledByANeighbour;
        using (var probe = AgentMemoryDiagnostics.Source.StartActivity(LiveSpan))
            sampledByANeighbour = probe is not null;
        listening.Clear();

        var results = await repo.SearchByTaskVectorAsync(
            Query, successFilter: true, limit: 10, scope: MemoryScope.For("owner-a"));

        results.Should().HaveCount(4);
        cyphers.Should().ContainSingle(because: "telemetry must never issue a query of its own");
        if (!sampledByANeighbour) listening.Spans.Should().BeEmpty();
    }

    // ── harness ────────────────────────────────────────────────────────

    private static SpanCapture Listen(
        ActivitySamplingResult sampling = ActivitySamplingResult.AllDataAndRecorded) => new(sampling);

    /// <summary>
    /// Drives the real repository against a runner that hands back <c>rowsPerQuery[n]</c> rows for the
    /// n-th vector query and records the Cypher of every query issued.
    /// </summary>
    private static (Neo4jReasoningTraceRepository Repo, List<string> Cyphers) CreateRepository(
        params int[] rowsPerQuery)
    {
        var cyphers = new List<string>();
        var tx = Substitute.For<INeo4jTransactionRunner>();
        tx.ReadAsync(
                Arg.Any<Func<IAsyncQueryRunner, Task<List<(ReasoningTrace, double)>>>>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<List<(ReasoningTrace, double)>>>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object>>())
                    .Returns(invocation =>
                    {
                        var index = cyphers.Count;
                        cyphers.Add(invocation.Arg<string>());
                        var rows = index < rowsPerQuery.Length ? rowsPerQuery[index] : 0;
                        return Task.FromResult((IResultCursor)new FakeResultCursor(
                            Enumerable.Range(0, rows).Select(TraceRecord).ToArray()));
                    });
                return await work(runner);
            });

        return (
            new Neo4jReasoningTraceRepository(tx, NullLogger<Neo4jReasoningTraceRepository>.Instance),
            cyphers);
    }

    private static IRecord TraceRecord(int index)
    {
        var properties = new Dictionary<string, object>
        {
            ["id"] = $"t-{index}",
            ["session_id"] = "session-1",
            ["task"] = "book a table",
            ["success"] = true,
            ["started_at"] = "2026-01-01T00:00:00.0000000+00:00",
        };

        var node = Substitute.For<INode>();
        foreach (var (key, value) in properties)
            node[key].Returns(value);
        node.Properties.Returns(properties);

        var record = Substitute.For<IRecord>();
        record["node"].Returns(node);
        record["score"].Returns(0.8d);
        return record;
    }

    /// <summary>Collects the trace yield spans raised while it is alive; disposing detaches it.</summary>
    private sealed class SpanCapture : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly List<Activity> _spans = new();

        internal SpanCapture(ActivitySamplingResult sampling)
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == AgentMemoryDiagnostics.SourceName,
                // Sample ONLY the two spans this class asserts on. Sampling is process-wide, so a listener
                // that samples everything forces creation of every AgentMemory span in every test class
                // running concurrently — which is exactly what breaks a neighbour's "no listener attached"
                // assertion. Scoping the sampler by name keeps this class from being that neighbour.
                Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                    options.Name is LiveSpan or AsOfSpan ? sampling : ActivitySamplingResult.None,
                ActivityStopped = activity =>
                {
                    if (activity.OperationName is not (LiveSpan or AsOfSpan)) return;
                    lock (_spans) _spans.Add(activity);
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        internal IReadOnlyList<Activity> Spans
        {
            get { lock (_spans) return _spans.ToList(); }
        }

        internal void Clear()
        {
            lock (_spans) _spans.Clear();
        }

        internal Activity Single(string spanName) =>
            Spans.Where(span => span.OperationName == spanName).Should().ContainSingle().Subject;

        public void Dispose() => _listener.Dispose();
    }
}
