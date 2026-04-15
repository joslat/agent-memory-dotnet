using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.AgentMemory.Neo4j.Repositories;
using Neo4j.Driver;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Repositories;

public sealed class Neo4jEntityRepositoryBatchTests
{
    private static (Neo4jEntityRepository Repo, List<(string Cypher, object? Parameters)> Calls)
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
        return (new Neo4jEntityRepository(txRunner, NullLogger<Neo4jEntityRepository>.Instance), calls);
    }

    private static (Neo4jEntityRepository Repo, List<(string Cypher, object? Parameters)> Calls)
        CreateEntityBatchWriteCapture()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<List<Entity>>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<List<Entity>>>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(ci =>
                    {
                        calls.Add((ci.Arg<string>(), ci.ArgAt<object>(1)));
                        var cursor = Substitute.For<IResultCursor>();
                        cursor.FetchAsync().Returns(Task.FromResult(false));
                        return Task.FromResult(cursor);
                    });
                return await work(runner);
            });
        return (new Neo4jEntityRepository(txRunner, NullLogger<Neo4jEntityRepository>.Instance), calls);
    }

    // ── CreateExtractedFromRelationshipAsync ──

    [Fact]
    public async Task CreateExtractedFromRelationshipAsync_SendsCorrectCypher()
    {
        var (repo, calls) = CreateWriteCapture();

        await repo.CreateExtractedFromRelationshipAsync("e-1", "msg-1");

        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("MERGE (e)-[r:EXTRACTED_FROM]->(m)");
        calls[0].Cypher.Should().Contain("r.confidence");
        calls[0].Cypher.Should().Contain("r.created_at = datetime()");
    }

    [Fact]
    public async Task CreateExtractedFromRelationshipAsync_PassesCorrectParameters()
    {
        var (repo, calls) = CreateWriteCapture();

        await repo.CreateExtractedFromRelationshipAsync("e-10", "msg-20");

        var parameters = calls[0].Parameters!;
        parameters.GetType().GetProperty("entityId")!.GetValue(parameters).Should().Be("e-10");
        parameters.GetType().GetProperty("messageId")!.GetValue(parameters).Should().Be("msg-20");
    }

    // ── UpsertBatchAsync ──

    [Fact]
    public async Task UpsertBatchAsync_EmptyList_ReturnsEmptyWithoutRunningCypher()
    {
        var (repo, calls) = CreateEntityBatchWriteCapture();

        var result = await repo.UpsertBatchAsync(Array.Empty<Entity>());

        result.Should().BeEmpty();
        calls.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertBatchAsync_SendsUnwindMergeCypher()
    {
        var (repo, calls) = CreateEntityBatchWriteCapture();
        var entities = new List<Entity>
        {
            new()
            {
                EntityId = "e1", Name = "Alice", Type = "Person", Confidence = 0.9,
                CreatedAtUtc = DateTimeOffset.UtcNow, SourceMessageIds = Array.Empty<string>(),
                Aliases = Array.Empty<string>(), Attributes = new Dictionary<string, object>(),
                Metadata = new Dictionary<string, object>()
            }
        };

        await repo.UpsertBatchAsync(entities);

        calls.Should().HaveCountGreaterThanOrEqualTo(1);
        calls[0].Cypher.Should().Contain("UNWIND $items AS item");
    }

    [Fact]
    public async Task UpsertBatchAsync_SendsExtractedFromForEntitiesWithSources()
    {
        var (repo, calls) = CreateEntityBatchWriteCapture();
        var entities = new List<Entity>
        {
            new()
            {
                EntityId = "e1", Name = "Alice", Type = "Person", Confidence = 0.9,
                CreatedAtUtc = DateTimeOffset.UtcNow, SourceMessageIds = new[] { "msg-1" },
                Aliases = Array.Empty<string>(), Attributes = new Dictionary<string, object>(),
                Metadata = new Dictionary<string, object>()
            }
        };

        await repo.UpsertBatchAsync(entities);

        // Merge + labels + EXTRACTED_FROM
        calls.Should().HaveCountGreaterThanOrEqualTo(2);
        calls.Should().Contain(c => c.Cypher.Contains("EXTRACTED_FROM"));
    }
}
