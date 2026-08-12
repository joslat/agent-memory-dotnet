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
            // Scoped to the two db spans, not the whole source. ActivityListener is process-global and
            // sampling is a UNION across listeners, so sampling everything here forces creation of
            // every AgentMemory span in every test class running concurrently -- which is exactly what
            // breaks a neighbour's "nothing is measured when no listener wants the data" assertion.
            // Measured: that neighbour failed roughly 1 run in 4 until this was scoped.
            //
            // BOTH names are needed, not just the one asserted on: the query-span wrapper is only
            // installed when the enclosing memory.db.tx activity exists, so declining the parent
            // silently yields zero query spans rather than the two this test is about.
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                options.Name is "memory.db.query" or "memory.db.tx"
                    ? ActivitySamplingResult.AllDataAndRecorded
                    : ActivitySamplingResult.None,
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

    [Fact]
    public async Task ReadAsync_TransactionSpanReportsLabelledEntryDelayEstimate()
    {
        var (runner, factory) = Create();
        var session = Substitute.For<IAsyncSession>();
        var driverRunner = Substitute.For<IAsyncQueryRunner>();
        factory.OpenSession(AccessMode.Read).Returns(session);
        session
            .ExecuteReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<int>>>())
            .Returns(async call =>
            {
                await Task.Delay(25);
                return await call.Arg<Func<IAsyncQueryRunner, Task<int>>>()(driverRunner);
            });

        Activity? transaction = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentMemoryDiagnostics.SourceName,
            // Scoped to this one span, not the whole source. ActivityListener is process-global and
            // sampling is a UNION across listeners, so sampling everything here forces creation of
            // every AgentMemory span in every test class running concurrently -- which is exactly what
            // breaks a neighbour's "nothing is measured when no listener wants the data" assertion.
            // Measured: that neighbour failed roughly 1 run in 4 until this was scoped.
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                options.Name == "memory.db.tx"
                    ? ActivitySamplingResult.AllDataAndRecorded
                    : ActivitySamplingResult.None,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "memory.db.tx")
                    transaction = activity;
            },
        };
        ActivitySource.AddActivityListener(listener);

        await runner.ReadAsync(_ => Task.FromResult(42));

        transaction.Should().NotBeNull();
        transaction!.GetTagItem("db.transaction_entry_ms_est")
            .Should().BeOfType<double>().Which.Should().BeGreaterThan(10);
    }

    [Fact]
    public async Task ExecuteAtomicWriteAsync_RepositoryCallsJoinOneExplicitTransaction()
    {
        var (runner, factory) = Create();
        var session = Substitute.For<IAsyncSession>();
        var transaction = Substitute.For<IAsyncTransaction>();
        factory.OpenSession(AccessMode.Write).Returns(session);
        session.BeginTransactionAsync(Arg.Any<Action<TransactionConfigBuilder>?>()).Returns(transaction);

        var result = await runner.ExecuteAtomicWriteAsync(async cancellationToken =>
        {
            var writeResult = await runner.WriteAsync(queryRunner =>
            {
                queryRunner.Should().BeSameAs(transaction);
                return Task.FromResult(20);
            }, cancellationToken);
            var readResult = await runner.ReadAsync(queryRunner =>
            {
                queryRunner.Should().BeSameAs(transaction);
                return Task.FromResult(22);
            }, cancellationToken);
            return writeResult + readResult;
        });

        result.Should().Be(42);
        factory.Received(1).OpenSession(AccessMode.Write);
        // Null config == the no-argument form; the runner always takes the config-taking overload now.
        await session.Received(1).BeginTransactionAsync(null);
        await transaction.Received(1).CommitAsync();
        await transaction.DidNotReceive().RollbackAsync();
        await session.DidNotReceive().ExecuteWriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<int>>>());
        await session.DidNotReceive().ExecuteReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<int>>>());
    }

    [Fact]
    public async Task ExecuteAtomicWriteAsync_CallbackFailure_RollsBackAndDoesNotCommit()
    {
        var (runner, factory) = Create();
        var session = Substitute.For<IAsyncSession>();
        var transaction = Substitute.For<IAsyncTransaction>();
        factory.OpenSession(AccessMode.Write).Returns(session);
        session.BeginTransactionAsync(Arg.Any<Action<TransactionConfigBuilder>?>()).Returns(transaction);

        var act = async () => await runner.ExecuteAtomicWriteAsync<int>(async cancellationToken =>
        {
            await runner.WriteAsync(queryRunner =>
            {
                queryRunner.Should().BeSameAs(transaction);
                return Task.CompletedTask;
            }, cancellationToken);
            throw new InvalidOperationException("injected persistence failure");
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("injected persistence failure");
        await transaction.Received(1).RollbackAsync();
        await transaction.DidNotReceive().CommitAsync();
    }
}
