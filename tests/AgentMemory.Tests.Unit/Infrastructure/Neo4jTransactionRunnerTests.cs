using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Infrastructure;

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
}
