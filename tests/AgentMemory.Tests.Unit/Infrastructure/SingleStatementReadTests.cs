using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AgentMemory.Tests.Unit.Infrastructure;

/// <summary>
/// DRT-6 (C-01): a read of one statement goes as an auto-commit query, with a managed read's retries (invariant I-5), on
/// the write unit's transaction when one is open, and as before when switched off or on a runner without the path.
/// </summary>
[Collection("Observability")]
public sealed class SingleStatementReadTests
{
    private const string Statement = "MATCH (n) RETURN n";
    private static readonly object Parameters = new { id = "x" };

    private static (Neo4jTransactionRunner Runner, INeo4jSessionFactory Factory) Create(
        Action<Neo4jOptions>? configure = null, TimeSpan? firstRetryDelay = null)
    {
        var options = new Neo4jOptions();
        configure?.Invoke(options);
        var factory = Substitute.For<INeo4jSessionFactory>();
        var runner = new Neo4jTransactionRunner(factory, NullLogger<Neo4jTransactionRunner>.Instance, Options.Create(options))
        {
            FirstRetryDelay = firstRetryDelay ?? TimeSpan.FromMilliseconds(1),
        };
        return (runner, factory);
    }

    private static IAsyncSession SessionReturning(IResultCursor cursor)
    {
        var session = Substitute.For<IAsyncSession>();
        session.RunAsync(Statement, Parameters).Returns(cursor);
        session.RunAsync(Statement, Parameters, Arg.Any<Action<TransactionConfigBuilder>>()).Returns(cursor);
        return session;
    }

    private static async Task<int> OneStatement(IAsyncQueryRunner runner)
    {
        var cursor = await runner.RunAsync(Statement, Parameters);
        await cursor.ConsumeAsync();
        return 7;
    }

    [Fact]
    public async Task TheStatementGoesOnTheSession_WithoutATransaction()
    {
        var (runner, factory) = Create();
        var session = SessionReturning(Substitute.For<IResultCursor>());
        factory.OpenSingleStatementSession(AccessMode.Read).Returns(session);

        var result = await runner.ReadSingleAsync(OneStatement);

        result.Should().Be(7);
        await session.Received(1).RunAsync(Statement, Parameters);
        await session.DidNotReceive().ExecuteReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<int>>>(), Arg.Any<Action<TransactionConfigBuilder>?>());
        await session.DidNotReceive().BeginTransactionAsync(Arg.Any<Action<TransactionConfigBuilder>>());
        factory.DidNotReceive().OpenSession(Arg.Any<AccessMode>());
        await session.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task TheConfiguredDeadline_TravelsWithTheStatement()
    {
        var (runner, factory) = Create(o => o.TransactionTimeout = TimeSpan.FromSeconds(5));
        var session = SessionReturning(Substitute.For<IResultCursor>());
        factory.OpenSingleStatementSession(AccessMode.Read).Returns(session);

        await runner.ReadSingleAsync(OneStatement);

        await session.Received(1).RunAsync(Statement, Parameters, Arg.Any<Action<TransactionConfigBuilder>>());
        await session.DidNotReceive().RunAsync(Statement, Parameters);
    }

    [Fact]
    public async Task ARetriableFailure_IsTriedAgainOnAFreshSession()
    {
        var (runner, factory) = Create();
        var failing = Substitute.For<IAsyncSession>();
        failing.RunAsync(Statement, Parameters).ThrowsAsync(new ServiceUnavailableException("the server went away"));
        var working = SessionReturning(Substitute.For<IResultCursor>());
        factory.OpenSingleStatementSession(AccessMode.Read).Returns(failing, working);

        var result = await runner.ReadSingleAsync(OneStatement);

        result.Should().Be(7);
        factory.Received(2).OpenSingleStatementSession(AccessMode.Read);
        await failing.Received(1).DisposeAsync();
        await working.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task AFailureThatIsNotRetriable_IsThrownAtOnce()
    {
        var (runner, factory) = Create();
        var session = Substitute.For<IAsyncSession>();
        session.RunAsync(Statement, Parameters).ThrowsAsync(new ClientException("Neo.ClientError.Statement.SyntaxError", "bad statement"));
        factory.OpenSingleStatementSession(AccessMode.Read).Returns(session);

        var act = () => runner.ReadSingleAsync(OneStatement);

        await act.Should().ThrowAsync<ClientException>();
        factory.Received(1).OpenSingleStatementSession(AccessMode.Read);
    }

    [Fact]
    public async Task NoTryStartsAfterTheRetryTime()
    {
        // The next try would start after the configured retry time: the failure is thrown, as a managed read's is.
        var (runner, factory) = Create(o => o.MaxTransactionRetryTime = TimeSpan.FromMilliseconds(100),
            firstRetryDelay: TimeSpan.FromMilliseconds(60));
        var session = Substitute.For<IAsyncSession>();
        session.RunAsync(Statement, Parameters).ThrowsAsync(new ServiceUnavailableException("down"));
        factory.OpenSingleStatementSession(AccessMode.Read).Returns(session);

        var act = () => runner.ReadSingleAsync(OneStatement);

        // 60 ms (48 to 72) fits in 100 ms; the second wait, 120 ms (96 to 144), does not.
        await act.Should().ThrowAsync<ServiceUnavailableException>();
        factory.Received(2).OpenSingleStatementSession(AccessMode.Read);
    }

    [Fact]
    public async Task CancellingDuringTheWait_StopsTheRetries()
    {
        // A wait that fits in the retry time, so the read is waiting, not giving up, when it is cancelled.
        var (runner, factory) = Create(o => o.MaxTransactionRetryTime = TimeSpan.FromMinutes(5), firstRetryDelay: TimeSpan.FromSeconds(30));
        var session = Substitute.For<IAsyncSession>();
        session.RunAsync(Statement, Parameters).ThrowsAsync(new ServiceUnavailableException("down"));
        factory.OpenSingleStatementSession(AccessMode.Read).Returns(session);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = () => runner.ReadSingleAsync(OneStatement, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        factory.Received(1).OpenSingleStatementSession(AccessMode.Read);
    }

    [Fact]
    public async Task SwitchedOff_TheReadIsAManagedTransaction_AsBefore()
    {
        var (runner, factory) = Create(o => o.SingleStatementReads = false);
        var session = Substitute.For<IAsyncSession>();
        factory.OpenSession(AccessMode.Read).Returns(session);
        session.ExecuteReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<int>>>(), Arg.Any<Action<TransactionConfigBuilder>?>())
            .Returns(ci => Task.FromResult(42));

        var result = await runner.ReadSingleAsync(OneStatement);

        result.Should().Be(42);
        factory.DidNotReceive().OpenSingleStatementSession(Arg.Any<AccessMode>());
    }

    [Fact]
    public async Task InsideAWriteUnit_TheReadRunsOnTheUnitsTransaction()
    {
        var (runner, factory) = Create();
        var writeSession = Substitute.For<IAsyncSession>();
        var transaction = Substitute.For<IAsyncTransaction>();
        var cursor = Substitute.For<IResultCursor>();
        transaction.RunAsync(Statement, Parameters).Returns(cursor);
        writeSession.BeginTransactionAsync(Arg.Any<Action<TransactionConfigBuilder>?>()).Returns(transaction);
        factory.OpenSession(AccessMode.Write).Returns(writeSession);

        var result = await runner.ExecuteAtomicWriteAsync(_ => runner.ReadSingleAsync(OneStatement));

        result.Should().Be(7);
        await transaction.Received(1).RunAsync(Statement, Parameters);
        factory.DidNotReceive().OpenSingleStatementSession(Arg.Any<AccessMode>());
    }

    [Fact]
    public async Task ARunnerWithoutThePath_ReadsAsBefore()
    {
        // A test double or a decorator that does not implement the single-statement reader keeps its own ReadAsync.
        var plain = Substitute.For<INeo4jTransactionRunner>();
        plain.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<int>>>(), Arg.Any<CancellationToken>()).Returns(5);

        var result = await plain.ReadSingleAsync(OneStatement);

        result.Should().Be(5);
        await plain.Received(1).ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<int>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheSpanIsTaggedAsAManagedReadIs_WithTheRetryCounted()
    {
        var (runner, factory) = Create();
        var failing = Substitute.For<IAsyncSession>();
        failing.RunAsync(Statement, Parameters).ThrowsAsync(new SessionExpiredException("expired"));
        var working = SessionReturning(Substitute.For<IResultCursor>());
        factory.OpenSingleStatementSession(AccessMode.Read).Returns(failing, working);

        var transactions = new List<Activity>();
        var queries = new List<Activity>();
        var mine = new ActivityTraceId[1];
        using var listener = new ActivityListener
        {
            // Scoped to the db spans of this test's trace: the listener is process-global.
            ShouldListenTo = source => source.Name == AgentMemoryDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                options.Name is "memory.db.tx" or "memory.db.query" || options.Name == "test.root"
                    ? ActivitySamplingResult.AllDataAndRecorded
                    : ActivitySamplingResult.None,
            ActivityStopped = activity =>
            {
                if (activity.TraceId != mine[0]) return;
                lock (transactions)
                    (activity.OperationName == "memory.db.tx" ? transactions : activity.OperationName == "memory.db.query" ? queries : null)?.Add(activity);
            },
        };
        ActivitySource.AddActivityListener(listener);

        using (var root = AgentMemoryDiagnostics.Source.StartActivity("test.root"))
        {
            mine[0] = root!.TraceId;
            await runner.ReadSingleAsync(OneStatement);
        }

        var tx = transactions.Should().ContainSingle().Subject;
        tx.GetTagItem("db.mode").Should().Be("read");
        tx.GetTagItem("db.single_statement").Should().Be(true);
        tx.GetTagItem("db.attempts").Should().Be(2);
        tx.GetTagItem("db.retries").Should().Be(1);
        tx.Events.Should().ContainSingle(e => e.Name == "db.attempt");
        queries.Should().OnlyContain(q => q.ParentSpanId == tx.SpanId);
    }
}
