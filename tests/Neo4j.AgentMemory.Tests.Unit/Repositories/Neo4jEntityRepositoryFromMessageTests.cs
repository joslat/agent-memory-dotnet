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

    // ── Edge cases ──

    [Fact]
    public async Task GetEntitiesFromMessageAsync_NonExistentMessage_ReturnsEmptyList()
    {
        var (repo, _) = CreateReadCapture();
        var result = await repo.GetEntitiesFromMessageAsync("msg-does-not-exist");
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetEntitiesFromMessageAsync_MultipleEntities_AllMappedCorrectly()
    {
        var node1 = CreateEntityNode("ent-1", "Alice");
        var node2 = CreateEntityNode("ent-2", "Bob");

        var record1 = Substitute.For<IRecord>();
        record1["e"].Returns(node1);

        var record2 = Substitute.For<IRecord>();
        record2["e"].Returns(node2);

        var (repo, _) = CreateReadCapture(record1, record2);
        var result = await repo.GetEntitiesFromMessageAsync("msg-1");

        result.Should().HaveCount(2);
        result.Select(e => e.Name).Should().BeEquivalentTo(new[] { "Alice", "Bob" });
    }

    [Fact]
    public async Task GetEntitiesFromMessageAsync_UsesReadTransaction()
    {
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<List<Entity>>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<Entity>()));
        var repo = new Neo4jEntityRepository(txRunner, NullLogger<Neo4jEntityRepository>.Instance);

        await repo.GetEntitiesFromMessageAsync("msg-1");

        await txRunner.Received(1).ReadAsync(
            Arg.Any<Func<IAsyncQueryRunner, Task<List<Entity>>>>(),
            Arg.Any<CancellationToken>());
    }

    // ── Helpers ──

    private static INode CreateEntityNode(string id, string name)
    {
        var node = Substitute.For<INode>();
        node["id"].Returns(id);
        node["name"].Returns(name);
        node["type"].Returns("PERSON");
        node["confidence"].Returns(0.9);
        node["created_at"].Returns(DateTimeOffset.UtcNow.ToString("O"));
        node.Properties.Returns(new Dictionary<string, object>
        {
            ["id"] = id,
            ["name"] = name,
            ["type"] = "PERSON",
            ["confidence"] = 0.9,
            ["created_at"] = DateTimeOffset.UtcNow.ToString("O")
        });
        return node;
    }
}
