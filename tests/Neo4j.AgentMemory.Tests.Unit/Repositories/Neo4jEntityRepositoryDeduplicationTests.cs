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

public sealed class Neo4jEntityRepositoryDeduplicationTests
{
    // ── G9: FindSimilarByEmbeddingAsync ──────────────────────────────

    [Fact]
    public async Task FindSimilarByEmbeddingAsync_UsesFindSimilarByEmbeddingQuery()
    {
        var (repo, calls) = CreateReadCapture(Array.Empty<IRecord>());
        await repo.FindSimilarByEmbeddingAsync("ent-1");
        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Be(EntityQueries.FindSimilarByEmbedding);
    }

    [Fact]
    public async Task FindSimilarByEmbeddingAsync_PassesEntityIdParameter()
    {
        var (repo, calls) = CreateReadCapture(Array.Empty<IRecord>());
        await repo.FindSimilarByEmbeddingAsync("ent-42");
        var param = calls[0].Parameters!;
        param.GetType().GetProperty("entityId")!.GetValue(param).Should().Be("ent-42");
    }

    [Fact]
    public async Task FindSimilarByEmbeddingAsync_PassesTopKAsLimitPlusOne()
    {
        var (repo, calls) = CreateReadCapture(Array.Empty<IRecord>());
        await repo.FindSimilarByEmbeddingAsync("ent-1", limit: 5);
        var param = calls[0].Parameters!;
        param.GetType().GetProperty("topK")!.GetValue(param).Should().Be(6);
    }

    [Fact]
    public async Task FindSimilarByEmbeddingAsync_PassesMinSimilarityParameter()
    {
        var (repo, calls) = CreateReadCapture(Array.Empty<IRecord>());
        await repo.FindSimilarByEmbeddingAsync("ent-1", minSimilarity: 0.9);
        var param = calls[0].Parameters!;
        param.GetType().GetProperty("minSimilarity")!.GetValue(param).Should().Be(0.9);
    }

    [Fact]
    public async Task FindSimilarByEmbeddingAsync_PassesLimitParameter()
    {
        var (repo, calls) = CreateReadCapture(Array.Empty<IRecord>());
        await repo.FindSimilarByEmbeddingAsync("ent-1", limit: 20);
        var param = calls[0].Parameters!;
        param.GetType().GetProperty("limit")!.GetValue(param).Should().Be(20);
    }

    [Fact]
    public async Task FindSimilarByEmbeddingAsync_ReturnsEmptyList_WhenNoResults()
    {
        var (repo, _) = CreateReadCapture(Array.Empty<IRecord>());
        var result = await repo.FindSimilarByEmbeddingAsync("ent-1");
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task FindSimilarByEmbeddingAsync_MapsResultsCorrectly()
    {
        var node = CreateEntityNode("ent-2", "Similar Entity");
        var record = Substitute.For<IRecord>();
        record["node"].Returns(node);
        record["score"].Returns(0.92);

        var (repo, _) = CreateReadCapture(new[] { record });
        var result = await repo.FindSimilarByEmbeddingAsync("ent-1");

        result.Should().ContainSingle();
        result[0].Entity.EntityId.Should().Be("ent-2");
        result[0].Entity.Name.Should().Be("Similar Entity");
        result[0].Similarity.Should().Be(0.92);
    }

    [Fact]
    public async Task FindSimilarByEmbeddingAsync_UsesDefaultParameters()
    {
        var (repo, calls) = CreateReadCapture(Array.Empty<IRecord>());
        await repo.FindSimilarByEmbeddingAsync("ent-1");
        var param = calls[0].Parameters!;
        param.GetType().GetProperty("minSimilarity")!.GetValue(param).Should().Be(0.85);
        param.GetType().GetProperty("limit")!.GetValue(param).Should().Be(10);
        param.GetType().GetProperty("topK")!.GetValue(param).Should().Be(11);
    }

    [Fact]
    public async Task FindSimilarByEmbeddingAsync_UsesReadTransaction()
    {
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<List<(Entity, double)>>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<(Entity, double)>()));
        var repo = new Neo4jEntityRepository(txRunner, NullLogger<Neo4jEntityRepository>.Instance);

        await repo.FindSimilarByEmbeddingAsync("ent-1");

        await txRunner.Received(1).ReadAsync(
            Arg.Any<Func<IAsyncQueryRunner, Task<List<(Entity, double)>>>>(),
            Arg.Any<CancellationToken>());
    }

    // ── G10: GetPendingDuplicatesAsync ───────────────────────────────

    [Fact]
    public async Task GetPendingDuplicatesAsync_UsesGetPendingDuplicatesQuery()
    {
        var (repo, calls) = CreateReadCapture(Array.Empty<IRecord>());
        await repo.GetPendingDuplicatesAsync();
        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Be(EntityQueries.GetPendingDuplicates);
    }

    [Fact]
    public async Task GetPendingDuplicatesAsync_PassesLimitParameter()
    {
        var (repo, calls) = CreateReadCapture(Array.Empty<IRecord>());
        await repo.GetPendingDuplicatesAsync(limit: 25);
        var param = calls[0].Parameters!;
        param.GetType().GetProperty("limit")!.GetValue(param).Should().Be(25);
    }

    [Fact]
    public async Task GetPendingDuplicatesAsync_ReturnsEmptyList_WhenNoResults()
    {
        var (repo, _) = CreateReadCapture(Array.Empty<IRecord>());
        var result = await repo.GetPendingDuplicatesAsync();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPendingDuplicatesAsync_MapsResultsCorrectly()
    {
        var sourceNode = CreateEntityNode("ent-1", "Source Entity");
        var targetNode = CreateEntityNode("ent-2", "Target Entity");
        var record = Substitute.For<IRecord>();
        record["a"].Returns(sourceNode);
        record["b"].Returns(targetNode);
        record["similarity"].Returns(0.95);
        record["status"].Returns("pending");

        var (repo, _) = CreateReadCapture(new[] { record });
        var result = await repo.GetPendingDuplicatesAsync();

        result.Should().ContainSingle();
        result[0].Source.EntityId.Should().Be("ent-1");
        result[0].Source.Name.Should().Be("Source Entity");
        result[0].Target.EntityId.Should().Be("ent-2");
        result[0].Target.Name.Should().Be("Target Entity");
        result[0].Similarity.Should().Be(0.95);
        result[0].Status.Should().Be("pending");
    }

    [Fact]
    public async Task GetPendingDuplicatesAsync_UsesDefaultLimit()
    {
        var (repo, calls) = CreateReadCapture(Array.Empty<IRecord>());
        await repo.GetPendingDuplicatesAsync();
        var param = calls[0].Parameters!;
        param.GetType().GetProperty("limit")!.GetValue(param).Should().Be(50);
    }

    [Fact]
    public async Task GetPendingDuplicatesAsync_UsesReadTransaction()
    {
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<List<DuplicatePair>>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<DuplicatePair>()));
        var repo = new Neo4jEntityRepository(txRunner, NullLogger<Neo4jEntityRepository>.Instance);

        await repo.GetPendingDuplicatesAsync();

        await txRunner.Received(1).ReadAsync(
            Arg.Any<Func<IAsyncQueryRunner, Task<List<DuplicatePair>>>>(),
            Arg.Any<CancellationToken>());
    }

    // ── G11: GetDeduplicationStatsAsync ──────────────────────────────

    [Fact]
    public async Task GetDeduplicationStatsAsync_UsesGetDeduplicationStatsQuery()
    {
        var record = CreateStatsRecord(0, 0, 0, 0);
        var (repo, calls) = CreateStatsReadCapture(new[] { record });
        await repo.GetDeduplicationStatsAsync();
        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Be(EntityQueries.GetDeduplicationStats);
    }

    [Fact]
    public async Task GetDeduplicationStatsAsync_ReturnsZeroStats_WhenNoResults()
    {
        var (repo, _) = CreateStatsReadCapture(Array.Empty<IRecord>());
        var result = await repo.GetDeduplicationStatsAsync();
        result.Should().Be(new DeduplicationStats(0, 0, 0, 0));
    }

    [Fact]
    public async Task GetDeduplicationStatsAsync_MapsResultsCorrectly()
    {
        var record = CreateStatsRecord(5, 3, 2, 1);
        var (repo, _) = CreateStatsReadCapture(new[] { record });
        var result = await repo.GetDeduplicationStatsAsync();

        result.PendingCount.Should().Be(5);
        result.ConfirmedCount.Should().Be(3);
        result.RejectedCount.Should().Be(2);
        result.MergedCount.Should().Be(1);
    }

    [Fact]
    public async Task GetDeduplicationStatsAsync_UsesReadTransaction()
    {
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<DeduplicationStats>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DeduplicationStats(0, 0, 0, 0)));
        var repo = new Neo4jEntityRepository(txRunner, NullLogger<Neo4jEntityRepository>.Instance);

        await repo.GetDeduplicationStatsAsync();

        await txRunner.Received(1).ReadAsync(
            Arg.Any<Func<IAsyncQueryRunner, Task<DeduplicationStats>>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetDeduplicationStatsAsync_PassesEmptyParameters()
    {
        var record = CreateStatsRecord(0, 0, 0, 0);
        var (repo, calls) = CreateStatsReadCapture(new[] { record });
        await repo.GetDeduplicationStatsAsync();
        calls[0].Parameters.Should().NotBeNull();
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static (Neo4jEntityRepository Repo, List<(string Cypher, object? Parameters)> Calls)
        CreateReadCapture(IRecord[] records)
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var txRunner = Substitute.For<INeo4jTransactionRunner>();

        txRunner
            .ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<List<(Entity, double)>>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<List<(Entity, double)>>>>();
                var runner = CreateRunner(calls, records);
                return await work(runner);
            });
        txRunner
            .ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<List<DuplicatePair>>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<List<DuplicatePair>>>>();
                var runner = CreateRunner(calls, records);
                return await work(runner);
            });

        return (new Neo4jEntityRepository(txRunner, NullLogger<Neo4jEntityRepository>.Instance), calls);
    }

    private static (Neo4jEntityRepository Repo, List<(string Cypher, object? Parameters)> Calls)
        CreateStatsReadCapture(IRecord[] records)
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var txRunner = Substitute.For<INeo4jTransactionRunner>();

        txRunner
            .ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<DeduplicationStats>>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<DeduplicationStats>>>();
                var runner = CreateRunner(calls, records);
                return work(runner);
            });

        return (new Neo4jEntityRepository(txRunner, NullLogger<Neo4jEntityRepository>.Instance), calls);
    }

    private static IAsyncQueryRunner CreateRunner(
        List<(string Cypher, object? Parameters)> calls,
        IRecord[] records)
    {
        var runner = Substitute.For<IAsyncQueryRunner>();
        runner
            .RunAsync(Arg.Any<string>(), Arg.Any<object>())
            .Returns(ci =>
            {
                calls.Add((ci.Arg<string>(), ci.ArgAt<object>(1)));
                return Task.FromResult((IResultCursor)new FakeResultCursor(records));
            });
        return runner;
    }

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

    private static IRecord CreateStatsRecord(int pending, int confirmed, int rejected, int merged)
    {
        var record = Substitute.For<IRecord>();
        record["pending"].Returns(pending);
        record["confirmed"].Returns(confirmed);
        record["rejected"].Returns(rejected);
        record["merged"].Returns(merged);
        return record;
    }
}
