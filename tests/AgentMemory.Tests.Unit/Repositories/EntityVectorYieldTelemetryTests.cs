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
/// The owner-scoped entity vector searches must report their own recall yield, exactly as the fact
/// search already does: how wide the global index was asked to go, and how many rows survived the
/// owner post-filter to reach the caller.
/// </summary>
/// <remarks>
/// The index is global and the owner filter runs <i>after</i> top-K, so a tenant's recall depends on how
/// its neighbours rank — measured at a mean of 7 own rows out of 60 candidates on a 50-owner corpus,
/// with one question receiving none at all. That study covered facts; entities run the same query shape
/// through the same global index and were entirely unreported until these spans.
/// <para>
/// Every width assertion cross-checks the tag against the query that was actually issued — the literal
/// baked into <c>db.index.vector.queryNodes('entity_embedding_idx', &lt;topK&gt;, …)</c> for the two
/// searches that interpolate it, and the bound <c>$topK</c> parameter for the similarity search that
/// passes it — so a tag can never drift into reporting an intention rather than the query that ran.
/// </para>
/// </remarks>
public sealed class EntityVectorYieldTelemetryTests
{
    private const string VectorSpan = "memory.recall.entity_vector";
    private const string SimilarSpan = "memory.recall.entity_similar_vector";
    private const string AsOfSpan = "memory.recall.entity_vector_as_of";
    private static readonly float[] Query = [0.1f, 0.2f, 0.3f];

    [Fact]
    public async Task AScopedEntitySearchReportsTheWidthItAskedForAndTheRowsItGotBack()
    {
        var (repo, queries) = CreateRepository(rows: 7);
        using var listening = Listen(VectorSpan);

        var results = await repo.SearchByVectorAsync(
            Query, limit: 10, scope: MemoryScope.For("owner-a"));

        results.Should().HaveCount(7);
        queries.Should().ContainSingle()
            .Which.Cypher.Should().Contain("db.index.vector.queryNodes('entity_embedding_idx', 60,");

        var span = listening.Single();
        span.GetTagItem("memory.vector.owner_scoped").Should().Be(true);
        span.GetTagItem("memory.vector.limit").Should().Be(10);
        span.GetTagItem("memory.vector.requested_topk").Should().Be(60);
        span.GetTagItem("memory.vector.effective_topk").Should().Be(60);
        span.GetTagItem("memory.vector.returned").Should().Be(7);
    }

    [Fact]
    public async Task AStarvedScopedEntitySearchIsVisibleAsAYieldOfZero()
    {
        // The shape this exists to surface: 60 candidates drawn from every tenant, none of them the
        // querying owner's, and the pipeline carries on with an empty entity set and no complaint.
        var (repo, _) = CreateRepository(rows: 0);
        using var listening = Listen(VectorSpan);

        await repo.SearchByVectorAsync(Query, limit: 10, scope: MemoryScope.For("owner-a"));

        var span = listening.Single();
        span.GetTagItem("memory.vector.owner_scoped").Should().Be(true);
        span.GetTagItem("memory.vector.requested_topk").Should().Be(60);
        span.GetTagItem("memory.vector.returned").Should().Be(0);
    }

    [Fact]
    public async Task AnUnscopedEntitySearchIsMarkedUnscoped()
    {
        // Unscoped there is no post-filter and therefore no starvation to report. The tag exists so an
        // aggregate over scoped searches cannot be silently diluted by unscoped ones.
        var (repo, queries) = CreateRepository(rows: 3);
        using var listening = Listen(VectorSpan);

        await repo.SearchByVectorAsync(Query, limit: 10, scope: null);

        queries.Should().ContainSingle()
            .Which.Cypher.Should().Contain("db.index.vector.queryNodes('entity_embedding_idx', 10,");

        var span = listening.Single();
        span.GetTagItem("memory.vector.owner_scoped").Should().Be(false);
        span.GetTagItem("memory.vector.requested_topk").Should().Be(10);
        span.GetTagItem("memory.vector.returned").Should().Be(3);
    }

    [Fact]
    public async Task AnEntitySearchThatNeverReachedTheIndexReportsNoYieldAtAll()
    {
        // A degraded (zero-dimension) embedding short-circuits before any query is issued. Emitting a
        // zero-yield reading for a search that never ran would manufacture starvation that did not happen.
        var (repo, queries) = CreateRepository(rows: 5);
        using var listening = Listen(VectorSpan);

        (await repo.SearchByVectorAsync([], limit: 10, scope: MemoryScope.For("owner-a")))
            .Should().BeEmpty();

        queries.Should().BeEmpty();
        listening.Spans.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedEntitySearchReportsNoYieldRatherThanAFalseZero()
    {
        // A query that threw measured nothing. Tagging it `returned = 0` would put an indistinguishable
        // row next to a genuine total-starvation reading, which is the one outcome worth avoiding most.
        var tx = Substitute.For<INeo4jTransactionRunner>();
        tx.ReadAsync(
                Arg.Any<Func<IAsyncQueryRunner, Task<List<(Entity, double)>>>>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new ServiceUnavailableException("index offline"));
        var repo = new Neo4jEntityRepository(tx, NullLogger<Neo4jEntityRepository>.Instance);
        using var listening = Listen(VectorSpan);

        var search = () => repo.SearchByVectorAsync(Query, limit: 10, scope: MemoryScope.For("owner-a"));
        await search.Should().ThrowAsync<ServiceUnavailableException>();

        var span = listening.Single();
        span.GetTagItem("memory.vector.returned").Should().BeNull();
    }

    [Fact]
    public async Task NothingIsMeasuredWhenNoListenerWantsTheData()
    {
        // Zero overhead: with sampling declined the Activity is never created, so no tag value is
        // computed and the search behaves exactly as it does with no diagnostics at all.
        var (repo, queries) = CreateRepository(rows: 4);
        using var listening = Listen(VectorSpan, ActivitySamplingResult.None);

        var results = await repo.SearchByVectorAsync(
            Query, limit: 10, scope: MemoryScope.For("owner-a"));

        results.Should().HaveCount(4);
        queries.Should().ContainSingle(because: "telemetry must never issue a query of its own");
        listening.Spans.Should().BeEmpty();
    }

    [Fact]
    public async Task TheSimilaritySearchReportsTheCallersLimitNotTheSelfSlotItAddsToTheWidth()
    {
        // FindSimilarByEmbedding probes the index with the source entity's OWN embedding, so the source
        // is guaranteed to come back as its own nearest neighbour and is then dropped by
        // `WHERE node.id <> $entityId`. The width therefore budgets limit+1 — one slot for the self-hit.
        // The caller still asked for 10 (the Cypher's own `LIMIT $limit` is 10), so `limit` is the
        // denominator a yield ratio needs; publishing the internal 11 would understate every reading.
        var (repo, queries) = CreateRepository(rows: 4);
        using var listening = Listen(SimilarSpan);

        var results = await repo.FindSimilarByEmbeddingAsync(
            "entity-1", minSimilarity: 0.85, limit: 10, scope: MemoryScope.For("owner-a"));

        results.Should().HaveCount(4);
        // max((10+1)*5, (10+1)+50) = 61, bound as $topK rather than baked into the Cypher.
        var issued = queries.Should().ContainSingle().Subject;
        issued.Parameters["topK"].Should().Be(61);
        issued.Parameters["limit"].Should().Be(10);

        var span = listening.Single();
        span.GetTagItem("memory.vector.owner_scoped").Should().Be(true);
        span.GetTagItem("memory.vector.limit").Should().Be(10);
        span.GetTagItem("memory.vector.requested_topk").Should().Be(61);
        span.GetTagItem("memory.vector.effective_topk").Should().Be(61);
        span.GetTagItem("memory.vector.returned").Should().Be(4);
    }

    [Fact]
    public async Task AnUnscopedSimilaritySearchStillCarriesTheSelfSlotInItsWidth()
    {
        // Unscoped the over-fetch collapses to limit+1: no post-filter to survive, but the self-hit is
        // still filtered out, so the extra slot remains. requested_topk must report 11 — the width the
        // index really received — while limit stays the caller's 10.
        var (repo, queries) = CreateRepository(rows: 2);
        using var listening = Listen(SimilarSpan);

        await repo.FindSimilarByEmbeddingAsync("entity-1", minSimilarity: 0.85, limit: 10, scope: null);

        queries.Should().ContainSingle().Subject.Parameters["topK"].Should().Be(11);

        var span = listening.Single();
        span.GetTagItem("memory.vector.owner_scoped").Should().Be(false);
        span.GetTagItem("memory.vector.limit").Should().Be(10);
        span.GetTagItem("memory.vector.requested_topk").Should().Be(11);
        span.GetTagItem("memory.vector.returned").Should().Be(2);
    }

    [Fact]
    public async Task TheAsOfSearchReportsItsYieldUnderItsOwnSpan()
    {
        // Same global index, same post-filter, different clock — and a separate span, because a
        // point-in-time search additionally discards everything created after the cutoff, so folding its
        // yield in with live recall would blame the owner filter for a temporal exclusion.
        var (repo, queries) = CreateRepository(rows: 5);
        using var listening = Listen(AsOfSpan);

        var results = await repo.SearchByVectorAsOfAsync(
            Query, DateTimeOffset.UtcNow, limit: 10, scope: MemoryScope.For("owner-a"));

        results.Should().HaveCount(5);
        queries.Should().ContainSingle()
            .Which.Cypher.Should().Contain("db.index.vector.queryNodes('entity_embedding_idx', 60,");

        var span = listening.Single();
        span.GetTagItem("memory.vector.owner_scoped").Should().Be(true);
        span.GetTagItem("memory.vector.limit").Should().Be(10);
        span.GetTagItem("memory.vector.requested_topk").Should().Be(60);
        span.GetTagItem("memory.vector.effective_topk").Should().Be(60);
        span.GetTagItem("memory.vector.returned").Should().Be(5);
    }

    [Fact]
    public async Task AnAsOfSearchThatNeverReachedTheIndexReportsNoYieldAtAll()
    {
        var (repo, queries) = CreateRepository(rows: 5);
        using var listening = Listen(AsOfSpan);

        (await repo.SearchByVectorAsOfAsync([], DateTimeOffset.UtcNow, limit: 10, scope: MemoryScope.For("owner-a")))
            .Should().BeEmpty();

        queries.Should().BeEmpty();
        listening.Spans.Should().BeEmpty();
    }

    [Theory]
    [InlineData(VectorSpan)]
    [InlineData(SimilarSpan)]
    [InlineData(AsOfSpan)]
    public async Task NoEntityPathClaimsAnEscalationItCannotPerform(string spanName)
    {
        // Unified vocabulary: every vector-recall span emits `escalated`, and a path that never
        // issues a second query emits `false`. Omitting it was locally tidier and globally worse -
        // an absent tag is indistinguishable from a site that emits no telemetry, so a consumer
        // counting escalations could not tell "did not escalate" from "not instrumented".
        // What must STILL be absent is `escalated_topk`: no second query ran, so there is no width
        // to report, and a width nobody asked for is not a measurement.
        var (repo, _) = CreateRepository(rows: 0);
        using var listening = Listen(spanName);

        var scope = MemoryScope.For("owner-a");
        _ = spanName switch
        {
            VectorSpan => await repo.SearchByVectorAsync(Query, limit: 10, scope: scope),
            SimilarSpan => await repo.FindSimilarByEmbeddingAsync("entity-1", limit: 10, scope: scope),
            _ => await repo.SearchByVectorAsOfAsync(Query, DateTimeOffset.UtcNow, limit: 10, scope: scope),
        };

        var span = listening.Single();
        span.GetTagItem("memory.vector.returned").Should().Be(0);
        span.GetTagItem("memory.vector.escalated").Should().Be(false);
        span.GetTagItem("memory.vector.escalated_topk").Should().BeNull();
    }

    // ── harness ────────────────────────────────────────────────────────

    private static SpanCapture Listen(
        string spanName,
        ActivitySamplingResult sampling = ActivitySamplingResult.AllDataAndRecorded) =>
        new(spanName, sampling);

    /// <summary>A query the repository actually issued, with whatever it bound to it.</summary>
    private sealed record RecordedQuery(string Cypher, IReadOnlyDictionary<string, object?> Parameters);

    /// <summary>
    /// Drives the real repository against a runner that hands back <paramref name="rows"/> rows for every
    /// vector query and records the Cypher and parameters of each one.
    /// </summary>
    private static (Neo4jEntityRepository Repo, List<RecordedQuery> Queries) CreateRepository(int rows)
    {
        var queries = new List<RecordedQuery>();
        var tx = Substitute.For<INeo4jTransactionRunner>();
        tx.ReadAsync(
                Arg.Any<Func<IAsyncQueryRunner, Task<List<(Entity, double)>>>>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<List<(Entity, double)>>>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                IResultCursor Record(string cypher, IReadOnlyDictionary<string, object?> parameters)
                {
                    queries.Add(new RecordedQuery(cypher, parameters));
                    return new FakeResultCursor(Enumerable.Range(0, rows).Select(EntityRecord).ToArray());
                }

                // Both binding styles are in use: a Dictionary when the owner filter adds $ownerId, an
                // anonymous object when it does not.
                runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object>>())
                    .Returns(i => Task.FromResult(Record(
                        i.ArgAt<string>(0),
                        i.ArgAt<IDictionary<string, object>>(1).ToDictionary(p => p.Key, p => (object?)p.Value))));
                runner.RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(i => Task.FromResult(Record(i.ArgAt<string>(0), ReadProperties(i.ArgAt<object>(1)))));

                return await work(runner);
            });

        return (new Neo4jEntityRepository(tx, NullLogger<Neo4jEntityRepository>.Instance), queries);
    }

    private static IReadOnlyDictionary<string, object?> ReadProperties(object parameters) =>
        parameters.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(parameters));

    private static IRecord EntityRecord(int index)
    {
        var properties = new Dictionary<string, object>
        {
            ["id"] = $"e-{index}",
            ["name"] = "Alice",
            ["type"] = "PERSON",
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
    /// <remarks>
    /// Sampling is decided per span <i>name</i>, not for the whole source. Test classes run in parallel and
    /// share one <see cref="ActivitySource"/>, and a single listener that samples everything forces an
    /// Activity into existence for every other class's call sites too — which is exactly what their own
    /// "nothing is measured when nobody is listening" tests assert cannot happen. Declining every name but
    /// the one under test keeps this capture invisible to them.
    /// </remarks>
    private sealed class SpanCapture : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly List<Activity> _spans = new();
        private readonly string _spanName;

        internal SpanCapture(string spanName, ActivitySamplingResult sampling)
        {
            _spanName = spanName;
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == AgentMemoryDiagnostics.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                    options.Name == spanName ? sampling : ActivitySamplingResult.None,
                ActivityStopped = activity =>
                {
                    if (activity.OperationName != _spanName) return;
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
