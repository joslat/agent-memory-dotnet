using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.AgentMemory.Neo4j.Repositories;
using Neo4j.Driver;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Repositories;

public sealed class Neo4jToolCallRepositoryTests
{
    private static (Neo4jToolCallRepository Repo, List<(string Cypher, object? Parameters)> Calls)
        CreateWriteCapture()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(ci =>
                    {
                        calls.Add((ci.Arg<string>(), ci.ArgAt<object>(1)));
                        return Task.FromResult(Substitute.For<IResultCursor>());
                    });
                return work(runner);
            });
        return (new Neo4jToolCallRepository(txRunner, NullLogger<Neo4jToolCallRepository>.Instance), calls);
    }

    // ── CreateTriggeredByRelationshipAsync ──

    [Fact]
    public async Task CreateTriggeredByRelationshipAsync_SendsCorrectCypher()
    {
        var (repo, calls) = CreateWriteCapture();

        await repo.CreateTriggeredByRelationshipAsync("tc-1", "msg-1");

        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("MERGE (tc)-[:TRIGGERED_BY]->(m)");
    }

    [Fact]
    public async Task CreateTriggeredByRelationshipAsync_PassesCorrectParameters()
    {
        var (repo, calls) = CreateWriteCapture();

        await repo.CreateTriggeredByRelationshipAsync("tc-5", "msg-9");

        var parameters = calls[0].Parameters!;
        parameters.GetType().GetProperty("toolCallId")!.GetValue(parameters).Should().Be("tc-5");
        parameters.GetType().GetProperty("messageId")!.GetValue(parameters).Should().Be("msg-9");
    }
}
