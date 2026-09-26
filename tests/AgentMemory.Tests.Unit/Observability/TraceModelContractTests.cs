using System.Diagnostics;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.Core.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Observability;

/// <summary>
/// The spans a turn produces (MemoryTelemetry's catalogue): names and attributes, never durations.
/// </summary>
[Collection("Observability")]
public sealed class TraceModelContractTests
{
    /// <summary>Records every AgentMemory span stopped under this test's own root trace.</summary>
    private sealed class Capture : IDisposable
    {
        private static readonly ActivitySource Root = new(nameof(TraceModelContractTests));
        private readonly ActivityListener _listener;
        private readonly Activity _root;
        private readonly List<Activity> _spans = [];

        public Capture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = s => s.Name is AgentMemoryDiagnostics.SourceName or nameof(TraceModelContractTests),
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = a => { lock (_spans) _spans.Add(a); },
            };
            ActivitySource.AddActivityListener(_listener);
            _root = Root.StartActivity("test-root")!;
        }

        public IReadOnlyList<Activity> Spans(string name)
        {
            lock (_spans) return _spans.Where(s => s.OperationName == name && s.TraceId == _root.TraceId).ToList();
        }

        public List<Activity> AnyTrace(string name)
        {
            lock (_spans) return _spans.Where(s => s.OperationName == name).ToList();
        }

        public void Dispose()
        {
            _root.Dispose();
            _listener.Dispose();
        }
    }

    // ---- memory.route: the recall policy's decision ----

    [Fact]
    public async Task The_recall_policy_decision_is_a_route_span()
    {
        using var capture = new Capture();
        var memory = Substitute.For<IMemoryService>();
        memory.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RecallResult { Context = new MemoryContext { SessionId = "s1", AssembledAtUtc = DateTimeOffset.UtcNow } });
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new[] { 1f });
        var sut = new Neo4jMemoryContextProvider(
            memory, embeddings, Substitute.For<IClock>(), Substitute.For<IIdGenerator>(),
            Options.Create(new MemoryOptions()), Options.Create(new ContextFormatOptions()),
            Options.Create(new AgentFrameworkOptions()), NullLogger<Neo4jMemoryContextProvider>.Instance);

        await sut.BuildContextAsync([new ChatMessage(ChatRole.User, "what do you know?")], "s1", "c1", CancellationToken.None, "alice");

        var route = capture.Spans(MemoryTelemetry.RouteSpan).Should().ContainSingle().Subject;
        route.GetTagItem(MemoryTelemetry.RouteShouldRecall).Should().Be(true);
        route.GetTagItem(MemoryTelemetry.RoutePolicy).Should().NotBeNull();
        route.GetTagItem(MemoryTelemetry.RouteCategories).Should().NotBeNull();
    }

    // ---- memory.embed: how many inputs, how many the cache answered, how many went out ----

    private sealed class Generator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(_ => new Embedding<float>(new[] { 1f }))));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task Every_embedding_request_is_a_span_that_counts_cache_hits()
    {
        using var capture = new Capture();
        var sut = new EmbeddingOrchestrator(new Generator(), NullLogger<EmbeddingOrchestrator>.Instance, new EmbeddingVectorCache(8));

        await sut.EmbedAsync("a");
        await sut.EmbedBatchAsync(["a", "b", "c"]);

        var spans = capture.Spans(MemoryTelemetry.EmbedSpan);
        spans.Should().HaveCount(2);
        var batch = spans[1];
        batch.GetTagItem(MemoryTelemetry.EmbedInputs).Should().Be(3);
        batch.GetTagItem(MemoryTelemetry.EmbedCacheHits).Should().Be(1);
        batch.GetTagItem(MemoryTelemetry.EmbedSent).Should().Be(2);
    }

    // ---- memory.db.query: lives until the result is read; the server's own timings; retries ----

    private static (Neo4jTransactionRunner Runner, INeo4jSessionFactory Factory) Runner()
    {
        var factory = Substitute.For<INeo4jSessionFactory>();
        return (new Neo4jTransactionRunner(factory, NullLogger<Neo4jTransactionRunner>.Instance), factory);
    }

    [Fact]
    public async Task A_query_span_ends_when_its_result_is_consumed_and_carries_server_timings()
    {
        using var capture = new Capture();
        var (runner, factory) = Runner();
        var session = Substitute.For<IAsyncSession>();
        var driverRunner = Substitute.For<IAsyncQueryRunner>();
        var cursor = Substitute.For<IResultCursor>();
        var summary = Substitute.For<IResultSummary>();
        summary.ResultAvailableAfter.Returns(TimeSpan.FromMilliseconds(7));
        summary.ResultConsumedAfter.Returns(TimeSpan.FromMilliseconds(3));
        cursor.ConsumeAsync().Returns(summary);
        driverRunner.RunAsync(EntityQueries.GetById).Returns(cursor);
        factory.OpenSession(AccessMode.Read).Returns(session);
        session.ExecuteReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<bool>>>(), Arg.Any<Action<TransactionConfigBuilder>?>())
            .Returns(ci => ci.Arg<Func<IAsyncQueryRunner, Task<bool>>>()(driverRunner));

        bool stoppedBeforeConsume = true;
        await runner.ReadAsync(async tx =>
        {
            var result = await tx.RunAsync(EntityQueries.GetById);
            stoppedBeforeConsume = capture.Spans("memory.db.query").Count > 0;
            await result.ConsumeAsync();
        });

        stoppedBeforeConsume.Should().BeFalse("the span ends when the result is read, not when the cursor is returned");
        var query = capture.Spans("memory.db.query").Should().ContainSingle().Subject;
        query.GetTagItem("db.server.available_ms").Should().Be(7.0);
        query.GetTagItem("db.server.consumed_ms").Should().Be(3.0);
        query.GetTagItem("db.system").Should().Be("neo4j");
    }

    [Fact]
    public async Task A_managed_transaction_retry_is_counted_on_the_transaction_span()
    {
        using var capture = new Capture();
        var (runner, factory) = Runner();
        var session = Substitute.For<IAsyncSession>();
        var driverRunner = Substitute.For<IAsyncQueryRunner>();
        factory.OpenSession(AccessMode.Read).Returns(session);
        // The driver retries a transient failure by calling the callback again.
        session.ExecuteReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<int>>>(), Arg.Any<Action<TransactionConfigBuilder>?>())
            .Returns(async ci =>
            {
                var work = ci.Arg<Func<IAsyncQueryRunner, Task<int>>>();
                await work(driverRunner);
                return await work(driverRunner);
            });

        await runner.ReadAsync(_ => Task.FromResult(1));

        var tx = capture.Spans("memory.db.tx").Should().ContainSingle().Subject;
        tx.GetTagItem("db.attempts").Should().Be(2);
        tx.GetTagItem("db.retries").Should().Be(1);
        tx.Events.Should().Contain(e => e.Name == "db.attempt");
    }

    [Fact]
    public async Task A_failed_transaction_records_the_exception_type_not_its_message()
    {
        using var capture = new Capture();
        var (runner, factory) = Runner();
        var session = Substitute.For<IAsyncSession>();
        factory.OpenSession(AccessMode.Read).Returns(session);
        session.ExecuteReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<int>>>(), Arg.Any<Action<TransactionConfigBuilder>?>())
            .Returns<Task<int>>(_ => throw new InvalidOperationException("secret-content"));

        var act = () => runner.ReadAsync(_ => Task.FromResult(1));
        await act.Should().ThrowAsync<InvalidOperationException>();

        var tx = capture.Spans("memory.db.tx").Should().ContainSingle().Subject;
        tx.Status.Should().Be(ActivityStatusCode.Error);
        tx.GetTagItem(MemoryTelemetry.ErrorType).Should().Be(typeof(InvalidOperationException).FullName);
        tx.StatusDescription.Should().NotContain("secret-content");
    }

    // ---- background work links back to the trace that caused it ----

    [Fact]
    public async Task Background_access_tracking_is_linked_to_the_recall_that_queued_it()
    {
        using var capture = new Capture();
        var decay = Substitute.For<IMemoryDecayService>();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, decay);
        using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var origin = Activity.Current!.Context;
        await using var channel = new MemoryAccessTrackingChannel(provider, Options.Create(new MemoryOptions()),
            NullLogger<MemoryAccessTrackingChannel>.Instance);

        channel.Track([("n1", MemoryNodeKind.Fact)]);
        for (var i = 0; i < 100 && channel.Counters.Written == 0; i++) await Task.Delay(10);

        List<Activity> background;
        lock (this) background = [];
        for (var i = 0; i < 50; i++)
        {
            background = AllSpans(capture, "memory.background.access_tracking");
            if (background.Count > 0) break;
            await Task.Delay(10);
        }
        var span = background.Should().ContainSingle().Subject;
        span.Links.Should().ContainSingle(l => l.Context.TraceId == origin.TraceId);
        span.ParentSpanId.Should().Be(default(ActivitySpanId), "it runs after the recall returned: its own trace");
    }

    // Background spans run in their own trace, so they are found by name, not by the test root.
    private static List<Activity> AllSpans(Capture capture, string name) => capture.AnyTrace(name);

    // ---- fingerprints: our own unregistered queries get a stable name; consumer text stays unknown ----

    [Fact]
    public void Unregistered_agentmemory_queries_get_a_stable_structural_name()
    {
        var a = CypherQueryRegistry.FingerprintFor("CALL db.index.vector.queryNodes('entity_embedding_idx', 60, $embedding) YIELD node, score WHERE node.x = 1 RETURN node");
        var b = CypherQueryRegistry.FingerprintFor("CALL db.index.vector.queryNodes('entity_embedding_idx', 424, $embedding) YIELD node, score WHERE node.x = 1 RETURN node");

        a.Should().StartWith("unregistered:entity_embedding_idx:");
        b.Should().Be(a, "top-K variants of one query share a name");
        CypherQueryRegistry.FingerprintFor("MATCH (e:Entity {name: $name}) WHERE e.flag RETURN e.id")
            .Should().StartWith("unregistered:Entity:");
        CypherQueryRegistry.FingerprintFor("MATCH (n:Customer) RETURN n")
            .Should().Be(CypherQueryRegistry.UnknownFingerprint, "consumer text names none of our labels");
    }

    [Theory]
    [InlineData("memory.recall.facts", "semantic")]
    [InlineData("memory.recall.fact_vector", "semantic")]
    [InlineData("memory.recall.recent", "working")]
    [InlineData("memory.recall.messages", "episodic")]
    [InlineData("memory.recall.entities", "entity")]
    [InlineData("memory.recall.preferences", "preference")]
    [InlineData("memory.recall.traces", "reasoning")]
    [InlineData("memory.recall.fact_vector_as_of", "bitemporal")]
    [InlineData("memory.recall.due", "prospective")]
    [InlineData("memory.recall.total", null)]
    public void Recall_legs_name_their_memory_type(string span, string? type)
    {
        MemoryTelemetry.MemoryTypeOfSpan(span).Should().Be(type);
    }

    [Fact]
    public void Every_recall_leg_shape_reports_its_result_count()
    {
        AgentMemory.Core.Services.MemoryContextAssembler.ResultCount(new List<int> { 1, 2 }).Should().Be(2);
        AgentMemory.Core.Services.MemoryContextAssembler.ResultCount(new[] { 1, 2, 3 }).Should().Be(3);
        AgentMemory.Core.Services.MemoryContextAssembler.ResultCount(
            new ProspectiveDueResult { Due = [Fact()], Expiring = [Fact(), Fact()] }).Should().Be(3);
        AgentMemory.Core.Services.MemoryContextAssembler.ResultCount(
            new AgentMemory.Core.Services.MemoryContextAssembler.RelevantMessageSearchResult([Message()], [])).Should().Be(1);
        AgentMemory.Core.Services.MemoryContextAssembler.ResultCount(
            new AgentMemory.Core.Services.ScoredFactSearchResult([Fact(), Fact()], [])).Should().Be(2);
        AgentMemory.Core.Services.MemoryContextAssembler.ResultCount(new { Items = new List<int> { 1 } })
            .Should().BeNull("an unknown shape reports no count rather than reflecting over it");
        AgentMemory.Core.Services.MemoryContextAssembler.ResultCount(null).Should().BeNull();
    }

    [Theory]
    [InlineData("memory.recall.fact_vector_as_of", "bitemporal")]
    [InlineData("memory.recall.entity_similar_vector", "entity")]
    [InlineData("memory.recall.graphrag", "entity")]
    public void Recall_spans_started_outside_the_assembler_carry_their_memory_type(string span, string type)
    {
        using var capture = new Capture();
        using (MemoryTelemetry.StartRecallSpan(span)) { }
        capture.Spans(span).Should().ContainSingle()
            .Which.GetTagItem(MemoryTelemetry.MemoryType).Should().Be(type);
    }

    private static Fact Fact() => new() { FactId = Guid.NewGuid().ToString("N"), Subject = "a", Predicate = "p", Object = "b", Confidence = 1, CreatedAtUtc = DateTimeOffset.UtcNow };

    private static Message Message() => new() { MessageId = "m", ConversationId = "c", SessionId = "s", Role = "user", Content = "x", TimestampUtc = DateTimeOffset.UtcNow };

    [Theory]
    [InlineData("MATCH (e:Entity {type: $type}) WHERE e.invalidated_at IS NULL AND (e.owner_id = $ownerId OR e.owner_id IS NULL) RETURN e", "EntityQueries.GetByType")]
    [InlineData("MATCH (n:Entity) WHERE (n.owner_id = $ownerId OR n.owner_id IS NULL) AND n.embedding IS NOT NULL WITH n, vector.similarity.cosine(n.embedding, $embedding) AS score WHERE score >= $minScore RETURN n AS node, score ORDER BY score DESC LIMIT $limit", "EntityQueries.OwnerScopedScan")]
    [InlineData("MATCH (n:Preference) WHERE n.embedding IS NOT NULL WITH n, vector.similarity.cosine(n.embedding, $embedding) AS score RETURN n AS node, score", "PreferenceQueries.OwnerScopedScan")]
    [InlineData("MATCH (f:Fact) WHERE f.embedding IS NOT NULL AND f.invalidated_at IS NULL WITH f, vector.similarity.cosine(f.embedding, $embedding) AS score RETURN f AS node, score", "FactQueries.OwnerScopedScan")]
    [InlineData("MATCH (t:ReasoningTrace) WHERE t.task_embedding IS NOT NULL WITH t, vector.similarity.cosine(t.task_embedding, $embedding) AS score RETURN t AS node, score", "ReasoningQueries.OwnerScopedScan")]
    [InlineData("MATCH (f:Fact) WHERE f.subject_key = $subjectKey AND f.predicate_key = $predicateKey AND f.object_key = $objectKey AND f.owner_key = $ownerKey RETURN f LIMIT 1", "FactQueries.FindByMergeKey")]
    [InlineData("CREATE VECTOR INDEX fact_embedding_idx IF NOT EXISTS FOR (n:Fact) ON (n.embedding) OPTIONS {indexConfig: {`vector.dimensions`: 1024, `vector.similarity_function`: 'cosine'}}", "SchemaQueries.CreateVectorIndex")]
    public void The_hot_method_built_queries_found_in_a_traced_session_have_names(string cypher, string name)
    {
        CypherQueryRegistry.FingerprintFor(cypher).Should().Be(name);
    }

    [Fact]
    public void Caller_supplied_cypher_is_never_given_a_structural_name()
    {
        const string consumer = "MATCH (e:Entity) WHERE e.name = 'Alice Smith' RETURN e";
        using (CypherQueryRegistry.ConsumerQueries())
            CypherQueryRegistry.FingerprintFor(consumer).Should().Be(CypherQueryRegistry.UnknownFingerprint);

        // Outside that scope (our own method-built text), literals are normalised: the name is a shape.
        CypherQueryRegistry.FingerprintFor("MATCH (e:Entity) WHERE e.name = 'Alice Smith' RETURN e")
            .Should().Be(CypherQueryRegistry.FingerprintFor("MATCH (e:Entity) WHERE e.name = 'Bob Jones' RETURN e"));
    }

    [Fact]
    public async Task A_result_nobody_reads_ends_when_the_next_query_starts()
    {
        using var capture = new Capture();
        var (runner, factory) = Runner();
        var session = Substitute.For<IAsyncSession>();
        var driverRunner = Substitute.For<IAsyncQueryRunner>();
        driverRunner.RunAsync(Arg.Any<string>()).Returns(Substitute.For<IResultCursor>());
        factory.OpenSession(AccessMode.Write).Returns(session);
        session.ExecuteWriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<bool>>>(), Arg.Any<Action<TransactionConfigBuilder>?>())
            .Returns(ci => ci.Arg<Func<IAsyncQueryRunner, Task<bool>>>()(driverRunner));

        int stoppedAfterSecondRun = -1;
        await runner.WriteAsync(async tx =>
        {
            await tx.RunAsync(EntityQueries.GetById);          // discarded, never read
            await tx.RunAsync(EntityQueries.GetById);
            stoppedAfterSecondRun = capture.Spans("memory.db.query").Count;
            return true;
        });

        stoppedAfterSecondRun.Should().Be(1, "the unread first result ended when the second query started");
        capture.Spans("memory.db.query").Should().HaveCount(2)
            .And.OnlyContain(q => Equals(q.GetTagItem("db.result.read"), "none"));
    }

    [Fact]
    public async Task A_result_read_in_part_is_tagged_partial_with_its_rows()
    {
        using var capture = new Capture();
        var (runner, factory) = Runner();
        var session = Substitute.For<IAsyncSession>();
        var driverRunner = Substitute.For<IAsyncQueryRunner>();
        var cursor = Substitute.For<IResultCursor>();
        cursor.FetchAsync().Returns(true);
        var record = Substitute.For<IRecord>();
        record.Values.Returns(new Dictionary<string, object>());
        cursor.Current.Returns(record);
        driverRunner.RunAsync(Arg.Any<string>()).Returns(cursor);
        factory.OpenSession(AccessMode.Write).Returns(session);
        session.ExecuteWriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<bool>>>(), Arg.Any<Action<TransactionConfigBuilder>?>())
            .Returns(ci => ci.Arg<Func<IAsyncQueryRunner, Task<bool>>>()(driverRunner));

        await runner.WriteAsync(async tx =>
        {
            var result = await tx.RunAsync(EntityQueries.GetById);
            return await result.FetchAsync();                   // one record, never drained
        });

        var query = capture.Spans("memory.db.query").Should().ContainSingle().Subject;
        query.GetTagItem("db.result.read").Should().Be("partial");
        query.GetTagItem("db.rows").Should().Be(1L);
    }

    [Fact]
    public async Task A_retry_ends_the_failed_attempts_unread_query_before_running_again()
    {
        using var capture = new Capture();
        var (runner, factory) = Runner();
        var session = Substitute.For<IAsyncSession>();
        var driverRunner = Substitute.For<IAsyncQueryRunner>();
        driverRunner.RunAsync(Arg.Any<string>()).Returns(Substitute.For<IResultCursor>());
        factory.OpenSession(AccessMode.Write).Returns(session);
        int openWhenRetryStarted = -1;
        session.ExecuteWriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<bool>>>(), Arg.Any<Action<TransactionConfigBuilder>?>())
            .Returns(async ci =>
            {
                var work = ci.Arg<Func<IAsyncQueryRunner, Task<bool>>>();
                await work(driverRunner);                        // attempt 1: its commit "fails" transiently
                return await work(new ProbeRunner(driverRunner, () => openWhenRetryStarted = capture.Spans("memory.db.query").Count));
            });

        await runner.WriteAsync(async tx =>
        {
            await tx.RunAsync(EntityQueries.GetById);
            return true;
        });

        openWhenRetryStarted.Should().Be(1, "attempt 1's unread query ended when attempt 2 began");
    }

    /// <summary>Runs a probe the first time the (retried) callback touches the runner.</summary>
    private sealed class ProbeRunner(IAsyncQueryRunner inner, Action probe) : IAsyncQueryRunner
    {
        private Action? _probe = probe;

        private Task<IResultCursor> Run(Func<Task<IResultCursor>> run)
        {
            Interlocked.Exchange(ref _probe, null)?.Invoke();
            return run();
        }

        public Task<IResultCursor> RunAsync(string query) => Run(() => inner.RunAsync(query));
        public Task<IResultCursor> RunAsync(string query, object parameters) => Run(() => inner.RunAsync(query, parameters));
        public Task<IResultCursor> RunAsync(string query, IDictionary<string, object> parameters) => Run(() => inner.RunAsync(query, parameters));
        public Task<IResultCursor> RunAsync(Query query) => Run(() => inner.RunAsync(query));
        public void Dispose() => inner.Dispose();
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

}
