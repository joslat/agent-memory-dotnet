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
/// The bitemporal ("as of") fact vector search must report its own recall yield, and must report it
/// under a span of its own so it is never aggregated with the live search.
/// </summary>
/// <remarks>
/// Both paths query the same global vector index and apply the owner filter after top-K, so both are
/// exposed to the starvation measured on the 50-owner corpus (mean 7 of 60 candidates, one question
/// zero). They are nonetheless different populations: the live path answers "what does recall yield
/// now", the as-of path answers "what did it yield at a past clock reading", over a corpus that was
/// smaller at that reading. Folding them together would let a regression in either hide inside the
/// other's average, so the span names are distinct and this suite asserts the separation directly.
/// <para>
/// The as-of path issues exactly one query — it has no empty-result escalation — so the tag set is
/// deliberately narrower than the live path's. See the assertions on
/// <c>memory.vector.effective_topk</c> for why that tag is absent rather than echoed.
/// </para>
/// </remarks>
// Serialized with the other observability tests. ActivitySource.AddActivityListener registers
// PROCESS-WIDE and sampling is the UNION across every registered listener, so a sibling class
// sampling the same span name concurrently causes an Activity this class then receives. That is
// what made NothingIsMeasuredWhenNoListenerWantsTheData fail roughly one full run in four while
// passing every time in isolation -- the signature of cross-class listener bleed, not a broken
// assertion. EntityVectorYieldTelemetryTests already carried this; its four siblings did not.
[Collection("Observability")]
public sealed class FactVectorAsOfYieldTelemetryTests
{
    private const string SpanName = "memory.recall.fact_vector_as_of";
    private const string LiveSpanName = "memory.recall.fact_vector";
    private static readonly float[] Query = [0.1f, 0.2f, 0.3f];
    private static readonly DateTimeOffset AsOf = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AScopedAsOfSearchReportsTheWidthItAskedForAndTheRowsItGotBack()
    {
        var (repo, cyphers) = CreateRepository(rowsPerQuery: 7);
        using var listening = Listen();

        var results = await repo.SearchByVectorAsOfAsync(
            Query, AsOf, limit: 10, scope: MemoryScope.For("owner-a"));

        results.Should().HaveCount(7);
        cyphers.Should().ContainSingle()
            .Which.Should().Contain("db.index.vector.queryNodes('fact_embedding_idx', 60,");

        var span = listening.Single(SpanName);
        span.GetTagItem("memory.vector.owner_scoped").Should().Be(true);
        span.GetTagItem("memory.vector.limit").Should().Be(10);
        span.GetTagItem("memory.vector.requested_topk").Should().Be(60);
        span.GetTagItem("memory.vector.returned").Should().Be(7);
    }

    [Fact]
    public async Task TheAsOfYieldIsReportedUnderItsOwnSpanExactlyOncePerSearch()
    {
        // The two searches answer different questions over corpora of different sizes. A shared span name
        // would let a collapse in one be averaged away by the other — precisely the silent degradation
        // this instrumentation exists to catch — so the names are asserted to differ here rather than
        // left as a coincidence of two string literals in two files.
        //
        // The separation is also enforced negatively by construction: every other test in this class
        // resolves its span by the as-of name, so reusing the live name would empty this suite outright.
        // It is not asserted by listening for the live name, because sampling that name from here would
        // force spans into the live suite running in parallel and fail it from the outside.
        SpanName.Should().NotBe(LiveSpanName);

        var (repo, _) = CreateRepository(rowsPerQuery: 4);
        using var listening = Listen();

        await repo.SearchByVectorAsOfAsync(Query, AsOf, limit: 10, scope: MemoryScope.For("owner-a"));

        listening.Names.Should()
            .ContainSingle(because: "one search publishes one reading, never two")
            .Which.Should().Be(SpanName);
    }

    [Fact]
    public async Task AShortButNonEmptyScopedResultIsReportedAsIs()
    {
        // The starvation shape: 60 candidates asked for, 1 row delivered, pipeline carries on. On the
        // as-of path there is no second pass to soften it, so the single reading is the whole story.
        var (repo, cyphers) = CreateRepository(rowsPerQuery: 1);
        using var listening = Listen();

        await repo.SearchByVectorAsOfAsync(Query, AsOf, limit: 10, scope: MemoryScope.For("owner-a"));

        cyphers.Should().ContainSingle();
        var span = listening.Single(SpanName);
        span.GetTagItem("memory.vector.requested_topk").Should().Be(60);
        span.GetTagItem("memory.vector.returned").Should().Be(1);
    }

    [Fact]
    public async Task AnEmptyScopedAsOfSearchIsReportedAsNotEscalatedBecauseItNeverCanBe()
    {
        // Total failure — zero rows for an owner-scoped search — and, unlike the live path, no retry is
        // issued. `escalated` is emitted as a measured false rather than omitted, so a consumer counting
        // "total-failure recalls that got no second chance" can find these instead of reading the absent
        // tag as an instrumentation gap.
        var (repo, cyphers) = CreateRepository(rowsPerQuery: [0, 3]);
        using var listening = Listen();

        var results = await repo.SearchByVectorAsOfAsync(
            Query, AsOf, limit: 10, scope: MemoryScope.For("owner-a"));

        results.Should().BeEmpty(because: "the as-of path does not widen and retry");
        cyphers.Should().ContainSingle(because: "escalation belongs to the live path only");

        var span = listening.Single(SpanName);
        span.GetTagItem("memory.vector.returned").Should().Be(0);
        span.GetTagItem("memory.vector.escalated").Should().Be(false);
        span.GetTagItem("memory.vector.escalated_topk").Should().BeNull();
    }

    [Fact]
    public async Task NoEffectiveWidthIsPublishedBecauseOnlyOneWidthEverRuns()
    {
        // `effective_topk` IS published here, equal to `requested_topk`, because uniformity across all
        // eight recall spans beats a locally tidier schema: `returned / effective_topk` must work on
        // every span without the consumer knowing which sites can escalate. What stays absent is
        // `escalated_topk` - no second query ran, so there is no width to report.
        var (repo, _) = CreateRepository(rowsPerQuery: 2);
        using var listening = Listen();

        await repo.SearchByVectorAsOfAsync(Query, AsOf, limit: 10, scope: MemoryScope.For("owner-a"));

        var span = listening.Single(SpanName);
        span.GetTagItem("memory.vector.effective_topk").Should().Be(60);
        span.GetTagItem("memory.vector.requested_topk").Should().Be(60);
    }

    [Fact]
    public async Task AnUnscopedAsOfSearchIsMarkedUnscoped()
    {
        // Unscoped there is no post-filter and so no starvation to report; the tag keeps an aggregate
        // over scoped searches from being diluted by searches that were never at risk.
        var (repo, cyphers) = CreateRepository(rowsPerQuery: 0);
        using var listening = Listen();

        await repo.SearchByVectorAsOfAsync(Query, AsOf, limit: 10, scope: null);

        cyphers.Should().ContainSingle()
            .Which.Should().Contain("db.index.vector.queryNodes('fact_embedding_idx', 10,");

        var span = listening.Single(SpanName);
        span.GetTagItem("memory.vector.owner_scoped").Should().Be(false);
        span.GetTagItem("memory.vector.requested_topk").Should().Be(10);
        span.GetTagItem("memory.vector.returned").Should().Be(0);
        span.GetTagItem("memory.vector.escalated").Should().Be(false);
    }

    [Fact]
    public async Task ThePairOfClocksTheSearchRanAgainstIsTheAsOfQueryThatWasActuallyIssued()
    {
        // Guards the span against drifting onto the live query: only the bitemporal Cypher carries the
        // transaction-clock predicate, so a tag set attached to the wrong query would fail here.
        var (repo, cyphers) = CreateRepository(rowsPerQuery: 3);
        using var listening = Listen();

        await repo.SearchByVectorAsOfAsync(Query, AsOf, limit: 10, scope: MemoryScope.For("owner-a"));

        cyphers.Should().ContainSingle()
            .Which.Should().Contain("node.created_at <= datetime($systemAsOf)");
        listening.Single(SpanName).GetTagItem("memory.vector.returned").Should().Be(3);
    }

    [Fact]
    public async Task ASearchThatNeverReachedTheIndexReportsNoYieldAtAll()
    {
        // A degraded (zero-dimension) embedding short-circuits before any query is issued. Publishing a
        // zero-yield reading for a search that never ran would manufacture starvation that did not happen.
        var (repo, cyphers) = CreateRepository(rowsPerQuery: 5);
        using var listening = Listen();

        (await repo.SearchByVectorAsOfAsync([], AsOf, limit: 10, scope: MemoryScope.For("owner-a")))
            .Should().BeEmpty();

        cyphers.Should().BeEmpty();
        listening.Spans.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedSearchReportsNoYieldRatherThanAFalseZero()
    {
        // A query that threw measured nothing. Tagging it `returned = 0` would place an indistinguishable
        // row next to a genuine total-starvation reading.
        var tx = Substitute.For<INeo4jTransactionRunner>();
        tx.ReadAsync(
                Arg.Any<Func<IAsyncQueryRunner, Task<List<(Fact, double)>>>>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new ServiceUnavailableException("index offline"));
        var repo = new Neo4jFactRepository(tx, NullLogger<Neo4jFactRepository>.Instance);
        using var listening = Listen();

        var search = () => repo.SearchByVectorAsOfAsync(
            Query, AsOf, limit: 10, scope: MemoryScope.For("owner-a"));
        await search.Should().ThrowAsync<ServiceUnavailableException>();

        var span = listening.Single(SpanName);
        span.GetTagItem("memory.vector.returned").Should().BeNull();
        // Absent, not false. A search that FAILED published no yield at all, and "escalated:
        // false" would assert something about a query that never completed - the same false-zero
        // this test exists to prevent, one tag over.
        span.GetTagItem("memory.vector.escalated").Should().BeNull();
    }

    [Fact]
    public async Task NothingIsMeasuredWhenNoListenerWantsTheData()
    {
        // Zero overhead: with sampling declined the Activity is never created, so no tag value is
        // computed and the search behaves exactly as it does with no diagnostics at all.
        var (repo, cyphers) = CreateRepository(rowsPerQuery: 4);
        using var listening = Listen(ActivitySamplingResult.None);

        var results = await repo.SearchByVectorAsOfAsync(
            Query, AsOf, limit: 10, scope: MemoryScope.For("owner-a"));

        results.Should().HaveCount(4);
        cyphers.Should().ContainSingle(because: "telemetry must never issue a query of its own");
        listening.Spans.Should().BeEmpty();
    }

    // ── harness ────────────────────────────────────────────────────────

    private static SpanCapture Listen(
        ActivitySamplingResult sampling = ActivitySamplingResult.AllDataAndRecorded) => new(sampling);

    /// <summary>
    /// Drives the real repository against a runner that hands back <c>rowsPerQuery[n]</c> rows for the
    /// n-th vector query and records the Cypher of every query issued, so "how many queries ran" is an
    /// observation rather than an assumption.
    /// </summary>
    private static (Neo4jFactRepository Repo, List<string> Cyphers) CreateRepository(params int[] rowsPerQuery)
    {
        var cyphers = new List<string>();
        var tx = Substitute.For<INeo4jTransactionRunner>();
        tx.ReadAsync(
                Arg.Any<Func<IAsyncQueryRunner, Task<List<(Fact, double)>>>>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<List<(Fact, double)>>>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object>>())
                    .Returns(invocation =>
                    {
                        var index = cyphers.Count;
                        cyphers.Add(invocation.Arg<string>());
                        var rows = index < rowsPerQuery.Length ? rowsPerQuery[index] : 0;
                        return Task.FromResult((IResultCursor)new FakeResultCursor(
                            Enumerable.Range(0, rows).Select(FactRecord).ToArray()));
                    });
                return await work(runner);
            });

        return (new Neo4jFactRepository(tx, NullLogger<Neo4jFactRepository>.Instance), cyphers);
    }

    private static IRecord FactRecord(int index)
    {
        var properties = new Dictionary<string, object>
        {
            ["id"] = $"f-{index}",
            ["subject"] = "Alice",
            ["predicate"] = "works_at",
            ["object"] = "Neo4j",
            ["confidence"] = 0.9d,
            ["created_at"] = "2026-01-01T00:00:00.0000000+00:00",
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

    /// <summary>
    /// Collects the as-of yield spans raised while it is alive; disposing detaches the listener.
    /// </summary>
    /// <remarks>
    /// The sampling decision is scoped to <see cref="SpanName"/> rather than taken for the whole source,
    /// and this is load-bearing, not tidiness. An <see cref="ActivityListener"/> is process-global and
    /// sampling is a union: if any listener asks for a span, the <see cref="Activity"/> is created and
    /// every listener on that source sees it stop. A listener here that sampled the source indiscriminately
    /// would therefore manufacture spans inside sibling suites running in parallel — including the ones
    /// that assert an unlistened search creates nothing at all — and fail them nondeterministically from
    /// another file. Sampling only this span keeps the blast radius to this class.
    /// </remarks>
    private sealed class SpanCapture : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly List<Activity> _spans = new();

        internal SpanCapture(ActivitySamplingResult sampling)
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == AgentMemoryDiagnostics.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                    options.Name == SpanName ? sampling : ActivitySamplingResult.None,
                ActivityStopped = activity =>
                {
                    if (activity.OperationName != SpanName) return;
                    lock (_spans) _spans.Add(activity);
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        internal IReadOnlyList<Activity> Spans
        {
            get { lock (_spans) return _spans.ToList(); }
        }

        internal IReadOnlyList<string> Names => Spans.Select(s => s.OperationName).ToList();

        internal Activity Single(string name) =>
            Spans.Where(s => s.OperationName == name).Should().ContainSingle().Subject;

        public void Dispose() => _listener.Dispose();
    }
}
