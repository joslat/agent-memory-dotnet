using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.AgentMemory.Neo4j.Queries;
using Neo4j.AgentMemory.Neo4j.Repositories;
using Neo4j.AgentMemory.Tests.Unit.TestHelpers;
using Neo4j.Driver;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Repositories;

/// <summary>
/// Unit tests for <see cref="Neo4jEntityRepository.GetEntitiesFromMessageAsync"/> (G5).
/// </summary>
public sealed class Neo4jEntityRepositoryFromMessageTests
{
    private static (Neo4jEntityRepository Repo, List<(string Cypher, object? Parameters)> Calls)
        CreateReadCapture(params IRecord[] records)
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<List<Entity>>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<List<Entity>>>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(ci =>
                    {
                        calls.Add((ci.Arg<string>(), ci.ArgAt<object>(1)));
                        return Task.FromResult((IResultCursor)new FakeResultCursor(records));
                    });
                return await work(runner);
            });
        return (new Neo4jEntityRepository(txRunner, NullLogger<Neo4jEntityRepository>.Instance), calls);
    }

    [Fact]
    public async Task GetEntitiesFromMessageAsync_SendsCorrectCypher()
    {
        var (repo, calls) = CreateReadCapture();
        await repo.GetEntitiesFromMessageAsync("msg-1");
        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Be(EntityQueries.GetEntitiesFromMessage);
    }

    [Fact]
    public async Task GetEntitiesFromMessageAsync_PassesMessageIdParameter()
    {
        var (repo, calls) = CreateReadCapture();
        await repo.GetEntitiesFromMessageAsync("msg-42");
        var param = calls[0].Parameters!;
        param.GetType().GetProperty("messageId")!.GetValue(param).Should().Be("msg-42");
    }

    [Fact]
    public async Task GetEntitiesFromMessageAsync_ReturnsEmptyWhenNoEntities()
    {
        var (repo, _) = CreateReadCapture();
        var result = await repo.GetEntitiesFromMessageAsync("msg-empty");
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetEntitiesFromMessageAsync_CypherUsesExtractedFromRelationship()
    {
        var (repo, calls) = CreateReadCapture();
        await repo.GetEntitiesFromMessageAsync("msg-1");
        calls[0].Cypher.Should().Contain("EXTRACTED_FROM");
        calls[0].Cypher.Should().Contain("MATCH (m:Message {id: $messageId})");
        calls[0].Cypher.Should().Contain("ORDER BY e.name");
    }
}
