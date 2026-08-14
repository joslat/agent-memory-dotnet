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
/// The owner-scoped fact vector search must report its own recall yield as a standing signal, not as a
/// one-off study: how wide it asked the global index to go, how many rows the caller actually received
/// after the owner post-filter, and whether the empty-result escalation fired.
/// </summary>
/// <remarks>
/// The starvation these tags exist to surface was measured on a 50-owner corpus: at an over-fetch of 60
/// the querying owner's own rows inside the global top-K came to a mean of 7, and one question received
/// none at all. Nothing in the running system reported that; these tags do.
/// <para>
/// Every assertion here cross-checks the tag against the width baked into the Cypher that was actually
/// issued (<c>db.index.vector.queryNodes('fact_embedding_idx', &lt;topK&gt;, …)</c>), so a tag can never
/// drift into reporting an intention rather than the query that ran.
/// </para>
/// </remarks>
// Serialized with the other observability tests. ActivitySource.AddActivityListener registers
// PROCESS-WIDE and sampling is the UNION across every registered listener, so a sibling class
// sampling the same span name concurrently causes an Activity this class then receives. That is
// what made NothingIsMeasuredWhenNoListenerWantsTheData fail roughly one full run in four while
// passing every time in isolation -- the signature of cross-class listener bleed, not a broken
// assertion. EntityVectorYieldTelemetryTests already carried this; its four siblings did not.
[Collection("Observability")]
public sealed class FactVectorYieldTelemetryTests
{
    private const string SpanName = "memory.recall.fact_vector";
    private static readonly float[] Query = [0.1f, 0.2f, 0.3f];

    [Fact]
    public async Task AScopedSearchReportsTheWidthItAskedForAndTheRowsItGotBack()
    {
        var (repo, cyphers) = CreateRepository(rowsPerQuery: 7);
        using var listening = Listen();

        var results = await repo.SearchByVectorAsync(
            Query, limit: 10, scope: MemoryScope.For("owner-a"));

        results.Should().HaveCount(7);
        cyphers.Should().ContainSingle()
            .Which.Should().Contain("db.index.vector.queryNodes('fact_embedding_idx', 60,");

        var span = listening.Single();
        span.GetTagItem("memory.vector.owner_scoped").Should().Be(true);
        span.GetTagItem("memory.vector.limit").Should().Be(10);
        span.GetTagItem("memory.vector.requested_topk").Should().Be(60);
        span.GetTagItem("memory.vector.returned").Should().Be(7);
        span.GetTagItem("memory.vector.escalated").Should().Be(false);
    }

    [Fact]
    public async Task AShortButNonEmptyScopedResultIsReportedWithoutEscalating()
    {
        // The starvation shape the study found: 60 candidates requested, 1 row delivered, and the
        // pipeline carries on silently. This must be visible without the escalation flag being set,
        // because escalation is deliberately reserved for the total-failure (empty) case.
        var (repo, _) = CreateRepository(rowsPerQuery: 1);
        using var listening = Listen();

        await repo.SearchByVectorAsync(Query, limit: 10, scope: MemoryScope.For("owner-a"));

        var span = listening.Single();
        span.GetTagItem("memory.vector.requested_topk").Should().Be(60);
        span.GetTagItem("memory.vector.returned").Should().Be(1);
        span.GetTagItem("memory.vector.escalated").Should().Be(false);
    }

    [Fact]
    public async Task AnEmptyScopedSearchReportsTheEscalationAndTheWidthItEscalatedTo()
    {
        var (repo, cyphers) = CreateRepository(rowsPerQuery: [0, 3]);
        using var listening = Listen();

        var results = await repo.SearchByVectorAsync(
            Query, limit: 10, scope: MemoryScope.For("owner-a"));

        results.Should().HaveCount(3);
        cyphers.Should().HaveCount(2);
        cyphers[1].Should().Contain("db.index.vector.queryNodes('fact_embedding_idx', 480,");

        var span = listening.Single();
        span.GetTagItem("memory.vector.requested_topk").Should().Be(60);
        span.GetTagItem("memory.vector.escalated").Should().Be(true);
        span.GetTagItem("memory.vector.escalated_topk").Should().Be(480);
        span.GetTagItem("memory.vector.returned").Should().Be(3);
    }

    [Fact]
    public async Task AnEscalationThatStillFindsNothingIsStillReportedAsHavingEscalated()
    {
        var (repo, _) = CreateRepository(rowsPerQuery: [0, 0]);
        using var listening = Listen();

        await repo.SearchByVectorAsync(Query, limit: 10, scope: MemoryScope.For("owner-a"));

        var span = listening.Single();
        span.GetTagItem("memory.vector.escalated").Should().Be(true);
        span.GetTagItem("memory.vector.escalated_topk").Should().Be(480);
        span.GetTagItem("memory.vector.returned").Should().Be(0);
    }

    [Fact]
    public async Task AnUnscopedSearchIsMarkedUnscopedAndCarriesNoEscalatedWidth()
    {
        // Unscoped, there is no post-filter and therefore no starvation to report. The tag exists so an
        // aggregate over scoped searches cannot be silently diluted by unscoped ones, and the escalated
        // width is absent rather than defaulted, because no escalation was ever attempted.
        var (repo, cyphers) = CreateRepository(rowsPerQuery: 0);
        using var listening = Listen();

        await repo.SearchByVectorAsync(Query, limit: 10, scope: null);

        cyphers.Should().ContainSingle()
            .Which.Should().Contain("db.index.vector.queryNodes('fact_embedding_idx', 10,");

        var span = listening.Single();
        span.GetTagItem("memory.vector.owner_scoped").Should().Be(false);
        span.GetTagItem("memory.vector.requested_topk").Should().Be(10);
        span.GetTagItem("memory.vector.returned").Should().Be(0);
        span.GetTagItem("memory.vector.escalated").Should().Be(false);
        span.GetTagItem("memory.vector.escalated_topk").Should().BeNull();
    }

    [Fact]
    public async Task ASearchThatNeverReachedTheIndexReportsNoYieldAtAll()
    {
        // A degraded (zero-dimension) embedding short-circuits before any query is issued. Emitting a
        // zero-yield reading for a search that never ran would manufacture starvation that did not happen.
        var (repo, cyphers) = CreateRepository(rowsPerQuery: 5);
        using var listening = Listen();

        (await repo.SearchByVectorAsync([], limit: 10, scope: MemoryScope.For("owner-a")))
            .Should().BeEmpty();

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
                Arg.Any<Func<IAsyncQueryRunner, Task<List<(Fact, double)>>>>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new ServiceUnavailableException("index offline"));
        var repo = new Neo4jFactRepository(tx, NullLogger<Neo4jFactRepository>.Instance);
        using var listening = Listen();

        var search = () => repo.SearchByVectorAsync(Query, limit: 10, scope: MemoryScope.For("owner-a"));
        await search.Should().ThrowAsync<ServiceUnavailableException>();

        var span = listening.Single();
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

        var results = await repo.SearchByVectorAsync(
            Query, limit: 10, scope: MemoryScope.For("owner-a"));

        results.Should().HaveCount(4);
        cyphers.Should().ContainSingle(because: "telemetry must never issue a query of its own");
        listening.Spans.Should().BeEmpty();
    }

    // ── harness ────────────────────────────────────────────────────────

    private static SpanCapture Listen(
        ActivitySamplingResult sampling = ActivitySamplingResult.AllDataAndRecorded) => new(sampling);

    /// <summary>
    /// Drives the real repository against a runner that hands back <c>rowsPerQuery[n]</c> rows for the
    /// n-th vector query, so the escalation branch is reachable without a database, and records the
    /// Cypher of every query issued.
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

    /// <summary>Collects the yield spans raised while it is alive; disposing detaches the listener.</summary>
    private sealed class SpanCapture : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly List<Activity> _spans = new();

        internal SpanCapture(ActivitySamplingResult sampling)
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == AgentMemoryDiagnostics.SourceName,
                // Scoped to this one span, not the whole source. ActivityListener is process-global and
                // sampling is a union across listeners, so a listener that samples everything forces
                // creation of every AgentMemory span in every test class running concurrently — which is
                // exactly what breaks a neighbour's "no listener attached" assertion. Scoping the sampler
                // by name keeps this class from being that neighbour.
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

        internal Activity Single() => Spans.Should().ContainSingle().Subject;

        public void Dispose() => _listener.Dispose();
    }
}
