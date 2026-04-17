using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.AgentMemory.Neo4j.Queries;
using Neo4j.AgentMemory.Neo4j.Repositories;
using Neo4j.AgentMemory.Tests.Unit.TestHelpers;
using Neo4j.Driver;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Repositories;

/// <summary>
/// Unit tests for <see cref="Neo4jMessageRepository.DeleteAsync"/> (G1 + G2).
/// </summary>
public sealed class Neo4jMessageRepositoryDeleteTests
{
    private static (Neo4jMessageRepository Repo, List<(string Cypher, object? Parameters)> Calls)
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
        return (new Neo4jMessageRepository(txRunner, NullLogger<Neo4jMessageRepository>.Instance), calls);
    }

    // ── G1: Cascade Delete ──

    [Fact]
    public async Task DeleteAsync_Cascade_SendsDetachDeleteCypher()
    {
        var (repo, calls) = CreateDeleteCapture(true);
        await repo.DeleteAsync("msg-1", cascade: true);
        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("MATCH (m:Message {id: $id})");
        calls[0].Cypher.Should().Contain("DETACH DELETE m");
    }

    [Fact]
    public async Task DeleteAsync_Cascade_IsDefault()
    {
        var (repo, calls) = CreateDeleteCapture(true);
        await repo.DeleteAsync("msg-1");
        calls[0].Cypher.Should().Contain("DETACH DELETE m");
    }

    [Fact]
    public async Task DeleteAsync_Cascade_UsesDeleteCascadeQuery()
    {
        var (repo, calls) = CreateDeleteCapture(true);
        await repo.DeleteAsync("msg-1", cascade: true);
        calls[0].Cypher.Should().Be(MessageQueries.DeleteCascade);
    }

    [Fact]
    public async Task DeleteAsync_Cascade_PassesIdParameter()
    {
        var (repo, calls) = CreateDeleteCapture(true);
        await repo.DeleteAsync("msg-42", cascade: true);
        var param = calls[0].Parameters!;
        param.GetType().GetProperty("id")!.GetValue(param).Should().Be("msg-42");
    }

    [Fact]
    public async Task DeleteAsync_Cascade_ReturnsTrue_WhenDeleted()
    {
        var (repo, _) = CreateDeleteCapture(true);
        var result = await repo.DeleteAsync("msg-1", cascade: true);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAsync_Cascade_ReturnsFalse_WhenNotFound()
    {
        var (repo, _) = CreateDeleteCapture(false);
        var result = await repo.DeleteAsync("msg-missing", cascade: true);
        result.Should().BeFalse();
    }

    // ── G2: Simple Delete (no cascade) ──

    [Fact]
    public async Task DeleteAsync_NoCascade_SendsSimpleDeleteCypher()
    {
        var (repo, calls) = CreateDeleteCapture(true);
        await repo.DeleteAsync("msg-1", cascade: false);
        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("MATCH (m:Message {id: $id})");
        calls[0].Cypher.Should().Contain("DELETE m");
        calls[0].Cypher.Should().NotContain("DETACH");
    }

    [Fact]
    public async Task DeleteAsync_NoCascade_UsesDeleteSimpleQuery()
    {
        var (repo, calls) = CreateDeleteCapture(true);
        await repo.DeleteAsync("msg-1", cascade: false);
        calls[0].Cypher.Should().Be(MessageQueries.DeleteSimple);
    }

    [Fact]
    public async Task DeleteAsync_NoCascade_PassesIdParameter()
    {
        var (repo, calls) = CreateDeleteCapture(true);
        await repo.DeleteAsync("msg-99", cascade: false);
        var param = calls[0].Parameters!;
        param.GetType().GetProperty("id")!.GetValue(param).Should().Be("msg-99");
    }

    [Fact]
    public async Task DeleteAsync_NoCascade_ReturnsTrue_WhenDeleted()
    {
        var (repo, _) = CreateDeleteCapture(true);
        var result = await repo.DeleteAsync("msg-1", cascade: false);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAsync_NoCascade_ReturnsFalse_WhenNotFound()
    {
        var (repo, _) = CreateDeleteCapture(false);
        var result = await repo.DeleteAsync("msg-missing", cascade: false);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_UsesWriteTransaction()
    {
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<bool>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));
        var repo = new Neo4jMessageRepository(txRunner, NullLogger<Neo4jMessageRepository>.Instance);

        await repo.DeleteAsync("msg-1");

        await txRunner.Received(1).WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<bool>>>(), Arg.Any<CancellationToken>());
    }
}
