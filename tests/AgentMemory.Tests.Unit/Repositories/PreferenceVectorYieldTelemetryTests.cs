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
/// The owner-scoped preference vector searches — live and as-of — must report their own recall yield,
/// exactly as the fact search does: how wide they asked the global index to go, and how many rows the
/// caller actually received after the owner post-filter ran inside Cypher.
/// </summary>
/// <remarks>
/// Neo4j's vector index is global, so the owner filter is a POST-filter on a top-K drawn from every
/// tenant. Measured on a 50-owner corpus at an over-fetch of 60, the querying owner's own rows inside
/// that global top-K came to a mean of 7 and one question received none at all. That degradation was
/// visible on the fact path only; on these two it was invisible.
/// <para>
/// Every width assertion cross-checks the tag against the width baked into the Cypher that was actually
/// issued (<c>db.index.vector.queryNodes('preference_embedding_idx', &lt;topK&gt;, …)</c>), so a tag can
/// never drift into reporting an intention rather than the query that ran.
/// </para>
/// </remarks>
public sealed class PreferenceVectorYieldTelemetryTests
{
    private const string LiveSpan = "memory.recall.preference_vector";
    private const string AsOfSpan = "memory.recall.preference_vector_as_of";
    private static readonly float[] Query = [0.1f, 0.2f, 0.3f];
    private static readonly DateTimeOffset AsOf = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AScopedLiveSearchReportsTheWidthItAskedForAndTheRowsItGotBack()
    {
        var (repo, cyphers) = CreateRepository(rowsPerQuery: 7);
        using var listening = Listen();

        var results = await repo.SearchByVectorAsync(Query, limit: 10, scope: MemoryScope.For("owner-a"));

        results.Should().HaveCount(7);
        cyphers.Should().ContainSingle()
            .Which.Should().Contain("db.index.vector.queryNodes('preference_embedding_idx', 60,");

        var span = listening.Single(LiveSpan);
        span.GetTagItem("memory.vector.owner_scoped").Should().Be(true);
        span.GetTagItem("memory.vector.limit").Should().Be(10);
        span.GetTagItem("memory.vector.requested_topk").Should().Be(60);
        span.GetTagItem("memory.vector.effective_topk").Should().Be(60);
        span.GetTagItem("memory.vector.returned").Should().Be(7);
    }

    [Fact]
    public async Task AStarvedButNonEmptyScopedResultIsVisibleAsAYield()
    {
        // The starvation shape the study found: 60 candidates requested, 1 row delivered, pipeline
        // carries on silently. On this path nothing reported it at all before these tags.
        var (repo, _) = CreateRepository(rowsPerQuery: 1);
        using var listening = Listen();

        await repo.SearchByVectorAsync(Query, limit: 10, scope: MemoryScope.For("owner-a"));

        var span = listening.Single(LiveSpan);
        span.GetTagItem("memory.vector.requested_topk").Should().Be(60);
        span.GetTagItem("memory.vector.returned").Should().Be(1);
    }

    [Fact]
    public async Task AnUnscopedLiveSearchIsMarkedUnscopedAndAsksOnlyForTheLimit()
    {
        // Unscoped there is no post-filter and so no starvation to report. The tag exists so an aggregate
        // over scoped searches cannot be silently diluted by unscoped ones.
        var (repo, cyphers) = CreateRepository(rowsPerQuery: 4);
        using var listening = Listen();

        await repo.SearchByVectorAsync(Query, limit: 10, scope: null);

        cyphers.Should().ContainSingle()
            .Which.Should().Contain("db.index.vector.queryNodes('preference_embedding_idx', 10,");

        var span = listening.Single(LiveSpan);
        span.GetTagItem("memory.vector.owner_scoped").Should().Be(false);
        span.GetTagItem("memory.vector.requested_topk").Should().Be(10);
        span.GetTagItem("memory.vector.effective_topk").Should().Be(10);
        span.GetTagItem("memory.vector.returned").Should().Be(4);
    }

    [Fact]
    public async Task AScopedAsOfSearchReportsItsOwnYieldUnderItsOwnSpanName()
    {
        var (repo, cyphers) = CreateRepository(rowsPerQuery: 2);
        using var listening = Listen();

        var results = await repo.SearchByVectorAsOfAsync(
            Query, AsOf, limit: 10, scope: MemoryScope.For("owner-a"));

        results.Should().HaveCount(2);
        cyphers.Should().ContainSingle()
            .Which.Should().Contain("db.index.vector.queryNodes('preference_embedding_idx', 60,");

        // A distinct span name, because a point-in-time recall and a live one answer different questions
        // and folding them together would make either one unreadable.
        var span = listening.Single(AsOfSpan);
        span.GetTagItem("memory.vector.owner_scoped").Should().Be(true);
        span.GetTagItem("memory.vector.limit").Should().Be(10);
        span.GetTagItem("memory.vector.requested_topk").Should().Be(60);
        span.GetTagItem("memory.vector.effective_topk").Should().Be(60);
        span.GetTagItem("memory.vector.returned").Should().Be(2);
    }

    [Fact]
    public async Task NeitherPreferencePathClaimsAnEscalationItDoesNotHave()
    {
        // NARROWED, not weakened. The LIVE preference search now retries an empty scoped result, because
        // that behaviour was measured to matter: with 500 more-similar foreign rows an owner-scoped
        // search returned 0 of the owner's 4 rows on the identical query shape, and the retry restored
        // all 4. The AS-OF path still issues exactly one query, so the invariant holds for it unchanged
        // and is what this test now covers. The live path's escalation is asserted separately.
        var (repo, cyphers) = CreateRepository(rowsPerQuery: [0, 0]);
        using var listening = Listen();

        await repo.SearchByVectorAsOfAsync(Query, AsOf, limit: 10, scope: MemoryScope.For("owner-a"));

        cyphers.Should().HaveCount(1, because: "the as-of path has no rescue, so one query and no more");
        foreach (var span in listening.Spans)
        {
            span.GetTagItem("memory.vector.returned").Should().Be(0);
            span.GetTagItem("memory.vector.escalated").Should().Be(false);
            span.GetTagItem("memory.vector.escalated_topk").Should().BeNull();
        }
    }


    [Fact]
    public async Task TheLivePreferenceSearchNowReportsTheEscalationItPerforms()
    {
        // The behaviour change asserted rather than assumed: an owner whose preferences exist must not
        // receive nothing because foreign rows filled the global top-K.
        var (repo, cyphers) = CreateRepository(rowsPerQuery: [0, 0]);
        using var listening = Listen();

        await repo.SearchByVectorAsync(Query, limit: 10, scope: MemoryScope.For("owner-a"));

        cyphers.Should().HaveCount(2, because: "exactly one retry - not zero, and not a loop");
        var span = listening.Spans.Single();
        span.GetTagItem("memory.vector.escalated").Should().Be(true);
        span.GetTagItem("memory.vector.requested_topk").Should().Be(60,
            "requested_topk stays the FIRST pass width so a consumer can see what was originally asked");
        span.GetTagItem("memory.vector.escalated_topk").Should().NotBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASearchThatNeverReachedTheIndexReportsNoYieldAtAll(bool asOf)
    {
        // A degraded (zero-dimension) embedding short-circuits before any query is issued. Emitting a
        // zero-yield reading for a search that never ran would manufacture starvation that did not happen.
        var (repo, cyphers) = CreateRepository(rowsPerQuery: 5);
        using var listening = Listen();

        var results = asOf
            ? await repo.SearchByVectorAsOfAsync([], AsOf, limit: 10, scope: MemoryScope.For("owner-a"))
            : await repo.SearchByVectorAsync([], limit: 10, scope: MemoryScope.For("owner-a"));

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
                Arg.Any<Func<IAsyncQueryRunner, Task<List<(Preference, double)>>>>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new ServiceUnavailableException("index offline"));
        var repo = new Neo4jPreferenceRepository(tx, NullLogger<Neo4jPreferenceRepository>.Instance);
        using var listening = Listen();

        var search = () => repo.SearchByVectorAsync(Query, limit: 10, scope: MemoryScope.For("owner-a"));
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

        var results = await repo.SearchByVectorAsync(Query, limit: 10, scope: MemoryScope.For("owner-a"));

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
    private static (Neo4jPreferenceRepository Repo, List<string> Cyphers) CreateRepository(
        params int[] rowsPerQuery)
    {
        var cyphers = new List<string>();
        var tx = Substitute.For<INeo4jTransactionRunner>();
        tx.ReadAsync(
                Arg.Any<Func<IAsyncQueryRunner, Task<List<(Preference, double)>>>>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<List<(Preference, double)>>>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object>>())
                    .Returns(invocation =>
                    {
                        var index = cyphers.Count;
                        cyphers.Add(invocation.Arg<string>());
                        var rows = index < rowsPerQuery.Length ? rowsPerQuery[index] : 0;
                        return Task.FromResult((IResultCursor)new FakeResultCursor(
                            Enumerable.Range(0, rows).Select(PreferenceRecord).ToArray()));
                    });
                return await work(runner);
            });

        return (new Neo4jPreferenceRepository(tx, NullLogger<Neo4jPreferenceRepository>.Instance), cyphers);
    }

    private static IRecord PreferenceRecord(int index)
    {
        var properties = new Dictionary<string, object>
        {
            ["id"] = $"p-{index}",
            ["category"] = "beverage",
            ["preference"] = "espresso, no sugar",
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

    /// <summary>Collects the preference yield spans raised while it is alive; disposing detaches it.</summary>
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
