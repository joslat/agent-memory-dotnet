using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Cli.Perf;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Infrastructure;

[Collection("Observability")]
public sealed class Neo4jTransactionRunnerTests
{
    private static (Neo4jTransactionRunner Runner, INeo4jSessionFactory Factory) Create()
    {
        var factory = Substitute.For<INeo4jSessionFactory>();
        var runner = new Neo4jTransactionRunner(factory, NullLogger<Neo4jTransactionRunner>.Instance);
        return (runner, factory);
    }

    [Fact]
    public async Task ReadAsync_PreCancelledToken_ThrowsAndNeverOpensSession()
    {
        var (runner, factory) = Create();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await runner.ReadAsync(_ => Task.FromResult(1), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        factory.DidNotReceive().OpenSession(Arg.Any<AccessMode>());
    }

    [Fact]
    public async Task WriteAsync_PreCancelledToken_ThrowsAndNeverOpensSession()
    {
        var (runner, factory) = Create();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await runner.WriteAsync(_ => Task.FromResult(1), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        factory.DidNotReceive().OpenSession(Arg.Any<AccessMode>());
    }

    [Fact]
    public async Task ReadAsync_NonCancelledToken_ExecutesWork()
    {
        var (runner, factory) = Create();
        var session = Substitute.For<IAsyncSession>();
        factory.OpenSession(AccessMode.Read).Returns(session);
        session
            .ExecuteReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<int>>>())
            .Returns(ci => ci.Arg<Func<IAsyncQueryRunner, Task<int>>>()(Substitute.For<IAsyncQueryRunner>()));

        var result = await runner.ReadAsync(_ => Task.FromResult(42), CancellationToken.None);

        result.Should().Be(42);
        factory.Received(1).OpenSession(AccessMode.Read);
    }

    [Fact]
    public async Task ReadAsync_MaterializedRows_ReportEstimatedPayloadOnTransaction()
    {
        var (runner, factory) = Create();
        var session = Substitute.For<IAsyncSession>();
        var driverRunner = Substitute.For<IAsyncQueryRunner>();
        var cursor = Substitute.For<IResultCursor>();
        var record = Substitute.For<IRecord>();
        record.Values.Returns(new Dictionary<string, object>
        {
            ["text"] = "abc",                    // 3 UTF-16 chars = 6 estimated bytes
            ["score"] = 0.5d,                    // double = 8
            ["ids"] = new long[] { 1L, 2L },     // two integers = 16
        });
        cursor.FetchAsync().Returns(Task.FromResult(true), Task.FromResult(false));
        cursor.Current.Returns(record);
        driverRunner.RunAsync("RETURN payload").Returns(cursor);
        factory.OpenSession(AccessMode.Read).Returns(session);
        session
            .ExecuteReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<int>>>())
            .Returns(ci => ci.Arg<Func<IAsyncQueryRunner, Task<int>>>()(driverRunner));

        using var collector = new PerfCollector();
        using (collector.BeginTurn("payload-test", 0, "measure"))
        {
            await runner.ReadAsync(async tx =>
            {
                var result = await tx.RunAsync("RETURN payload");
                while (await result.FetchAsync())
                    _ = result.Current;
                return 1;
            });
        }

        var measured = collector.Records.Should().ContainSingle().Subject;
        measured.Counter("neo4j.records").Should().Be(1);
        measured.Counter("neo4j.bytes_est").Should().Be(30);
    }

    [Fact]
    public async Task ReadAsync_WithoutListener_PassesOriginalRunnerAndCursorThrough()
    {
        var (runner, factory) = Create();
        var session = Substitute.For<IAsyncSession>();
        var driverRunner = Substitute.For<IAsyncQueryRunner>();
        var driverCursor = Substitute.For<IResultCursor>();
        driverRunner.RunAsync("RETURN payload").Returns(driverCursor);
        factory.OpenSession(AccessMode.Read).Returns(session);
        session
            .ExecuteReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<bool>>>())
            .Returns(ci => ci.Arg<Func<IAsyncQueryRunner, Task<bool>>>()(driverRunner));

        await runner.ReadAsync(async tx =>
        {
            tx.Should().BeSameAs(driverRunner);
            var cursor = await tx.RunAsync("RETURN payload");
            cursor.Should().BeSameAs(driverCursor);
        });
    }

    [Fact]
    public async Task ReadAsync_QuerySpansCarryKnownOrUnknownFingerprintWithoutCypherText()
    {
        var (runner, factory) = Create();
        var session = Substitute.For<IAsyncSession>();
        var driverRunner = Substitute.For<IAsyncQueryRunner>();
        var driverCursor = Substitute.For<IResultCursor>();
        const string rawCypherSentinel = "RETURN 'm06-sensitive-sentinel' AS value";

        driverRunner.RunAsync(EntityQueries.GetById).Returns(driverCursor);
        driverRunner.RunAsync(rawCypherSentinel).Returns(driverCursor);
        factory.OpenSession(AccessMode.Read).Returns(session);
        session
            .ExecuteReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<bool>>>())
            .Returns(ci => ci.Arg<Func<IAsyncQueryRunner, Task<bool>>>()(driverRunner));

        var queryActivities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentMemoryDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "memory.db.query")
                    queryActivities.Add(activity);
            },
        };
        ActivitySource.AddActivityListener(listener);

        await runner.ReadAsync(async tx =>
        {
            await tx.RunAsync(EntityQueries.GetById);
            await tx.RunAsync(rawCypherSentinel);
        });

        queryActivities.Should().HaveCount(2);
        queryActivities.Select(activity => activity.GetTagItem("db.query.fingerprint"))
            .Should().Equal("EntityQueries.GetById", "unknown");

        foreach (var activity in queryActivities)
        {
            activity.Tags.Select(tag => tag.Value)
                .Should().NotContain(rawCypherSentinel,
                    because: "raw Cypher can contain parameters and must never enter telemetry artifacts");
        }
    }
}
