using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.AgentMemory.Neo4j.Repositories;
using Neo4j.AgentMemory.Tests.Unit.TestHelpers;
using Neo4j.Driver;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Repositories;

public sealed class Neo4jEntityRepositoryDeleteTests
{
    private static (Neo4jEntityRepository Repo, List<(string Cypher, object? Parameters)> Calls)
        CreateDeleteCapture(bool deleted)
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<bool>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<bool>>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                var record = Substitute.For<IRecord>();
                record["deleted"].Returns((object)deleted);
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(ci =>
                    {
                        calls.Add((ci.Arg<string>(), ci.ArgAt<object>(1)));
                        return Task.FromResult((IResultCursor)new FakeResultCursor(record));
                    });
                return await work(runner);
            });
        return (new Neo4jEntityRepository(txRunner, NullLogger<Neo4jEntityRepository>.Instance), calls);
    }

    [Fact]
    public async Task DeleteAsync_SendsDetachDeleteCypher()
    {
        var (repo, calls) = CreateDeleteCapture(true);
        await repo.DeleteAsync("ent-1");
        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("MATCH (e:Entity {id: $entityId})");
        calls[0].Cypher.Should().Contain("DETACH DELETE e");
    }

    [Fact]
    public async Task DeleteAsync_CypherReturnsDeletedFlag()
    {
        var (repo, calls) = CreateDeleteCapture(true);
        await repo.DeleteAsync("ent-1");
        calls[0].Cypher.Should().Contain("count(e) > 0 AS deleted");
    }

    [Fact]
    public async Task DeleteAsync_PassesEntityIdParameter()
    {
        var (repo, calls) = CreateDeleteCapture(true);
        await repo.DeleteAsync("ent-42");
        var param = calls[0].Parameters!;
        param.GetType().GetProperty("entityId")!.GetValue(param).Should().Be("ent-42");
    }

    [Fact]
    public async Task DeleteAsync_ReturnsTrue_WhenEntityDeleted()
    {
        var (repo, _) = CreateDeleteCapture(true);
        var result = await repo.DeleteAsync("ent-1");
        result.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAsync_ReturnsFalse_WhenEntityNotFound()
    {
        var (repo, _) = CreateDeleteCapture(false);
        var result = await repo.DeleteAsync("ent-nonexistent");
        result.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_UsesWriteTransaction()
    {
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<bool>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));
        var repo = new Neo4jEntityRepository(txRunner, NullLogger<Neo4jEntityRepository>.Instance);

        await repo.DeleteAsync("ent-1");

        await txRunner.Received(1).WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<bool>>>(), Arg.Any<CancellationToken>());
    }
}
