using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.AgentMemory.Neo4j.Repositories;
using Neo4j.Driver;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Repositories;

/// <summary>
/// Unit tests for Phase-2 entity resolution methods on Neo4jEntityRepository.
/// Cypher strings and parameter objects are captured via NSubstitute — no real Neo4j connection.
/// </summary>
public sealed class Neo4jEntityRepositoryExtensionsTests
{
    // helpers

    private static Neo4jEntityRepository CreateWriteCapture(List<(string Cypher, object? Parameters)> calls)
    {
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
        return new Neo4jEntityRepository(txRunner, NullLogger<Neo4jEntityRepository>.Instance);
    }

    // Lambda returns List<Entity> — that is the T inferred by the compiler for ReadAsync<T>.
    private static (Neo4jEntityRepository Repo, List<(string Cypher, object? Parameters)> Calls)
        CreateEntityListReadCapture()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<List<Entity>>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<List<Entity>>>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                var cursor = Substitute.For<IResultCursor>();
                cursor.FetchAsync().Returns(Task.FromResult(false));
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(ci =>
                    {
                        calls.Add((ci.Arg<string>(), ci.ArgAt<object>(1)));
                        return Task.FromResult(cursor);
                    });
                return await work(runner);
            });
        return (new Neo4jEntityRepository(txRunner, NullLogger<Neo4jEntityRepository>.Instance), calls);
    }

    // Lambda returns List<(Entity,double,string)> — T inferred for the SAME_AS ReadAsync<T>.
    private static (Neo4jEntityRepository Repo, List<(string Cypher, object? Parameters)> Calls)
        CreateSameAsReadCapture()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .ReadAsync(
                Arg.Any<Func<IAsyncQueryRunner, Task<List<(Entity Entity, double Confidence, string MatchType)>>>>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<List<(Entity, double, string)>>>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                var cursor = Substitute.For<IResultCursor>();
                cursor.FetchAsync().Returns(Task.FromResult(false));
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(ci =>
                    {
                        calls.Add((ci.Arg<string>(), ci.ArgAt<object>(1)));
                        return Task.FromResult(cursor);
                    });
                return await work(runner);
            });
        return (new Neo4jEntityRepository(txRunner, NullLogger<Neo4jEntityRepository>.Instance), calls);
    }

    // GetByTypeAsync

    [Fact]
    public async Task GetByTypeAsync_SendsCorrectCypher()
    {
        var (repo, calls) = CreateEntityListReadCapture();
        await repo.GetByTypeAsync("PERSON");
        calls.Should().HaveCount(1);
        calls[0].Cypher.Should().Contain("MATCH (e:Entity {type: $type})");
    }

    [Fact]
    public async Task GetByTypeAsync_PassesTypeParameter()
    {
        var (repo, calls) = CreateEntityListReadCapture();
        await repo.GetByTypeAsync("ORGANIZATION");
        calls.Should().HaveCount(1);
        calls[0].Parameters!.GetType().GetProperty("type")!.GetValue(calls[0].Parameters).Should().Be("ORGANIZATION");
    }

    // SearchByNameAsync

    [Fact]
    public async Task SearchByNameAsync_WithoutType_OmitsTypeConstraint()
    {
        var (repo, calls) = CreateEntityListReadCapture();
        await repo.SearchByNameAsync("Alice");
        calls.Should().HaveCount(1);
        calls[0].Cypher.Should().Contain("toLower(e.name) CONTAINS toLower($name)");
        calls[0].Cypher.Should().NotContain("{type: $type}");
    }

    [Fact]
    public async Task SearchByNameAsync_WithType_IncludesTypeConstraint()
    {
        var (repo, calls) = CreateEntityListReadCapture();
        await repo.SearchByNameAsync("Alice", "PERSON");
        calls.Should().HaveCount(1);
        calls[0].Cypher.Should().Contain("e.type = $type");
        calls[0].Cypher.Should().Contain("toLower(e.name) CONTAINS toLower($name)");
    }

    // AddMentionAsync

    [Fact]
    public async Task AddMentionAsync_SendsCorrectCypher()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var repo = CreateWriteCapture(calls);
        await repo.AddMentionAsync("msg-1", "ent-1");
        calls.Should().HaveCount(1);
        calls[0].Cypher.Should().Contain("MATCH (m:Message {id: $messageId})");
        calls[0].Cypher.Should().Contain("MATCH (e:Entity {id: $entityId})");
        calls[0].Cypher.Should().Contain("MERGE (m)-[r:MENTIONS]->(e)");
        calls[0].Cypher.Should().Contain("r.confidence = $confidence");
        calls[0].Cypher.Should().Contain("r.start_pos = $startPos");
        calls[0].Cypher.Should().Contain("r.end_pos = $endPos");
    }

    [Fact]
    public async Task AddMentionAsync_PassesCorrectParameters()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var repo = CreateWriteCapture(calls);
        await repo.AddMentionAsync("msg-42", "ent-99");
        var param = calls[0].Parameters!;
        param.GetType().GetProperty("messageId")!.GetValue(param).Should().Be("msg-42");
        param.GetType().GetProperty("entityId")!.GetValue(param).Should().Be("ent-99");
    }

    // AddMentionsBatchAsync

    [Fact]
    public async Task AddMentionsBatchAsync_SendsCorrectCypher()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var repo = CreateWriteCapture(calls);
        await repo.AddMentionsBatchAsync("msg-1", ["ent-1", "ent-2", "ent-3"]);
        calls.Should().HaveCount(1);
        calls[0].Cypher.Should().Contain("UNWIND $entityIds AS eid");
        calls[0].Cypher.Should().Contain("MATCH (e:Entity {id: eid})");
        calls[0].Cypher.Should().Contain("MERGE (m)-[r:MENTIONS]->(e)");
        calls[0].Cypher.Should().Contain("r.confidence = $confidence");
    }

    [Fact]
    public async Task AddMentionsBatchAsync_PassesEntityIdsList()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var repo = CreateWriteCapture(calls);
        await repo.AddMentionsBatchAsync("msg-batch", ["e1", "e2", "e3"]);
        var param = calls[0].Parameters!;
        var entityIds = param.GetType().GetProperty("entityIds")!.GetValue(param) as System.Collections.IList;
        entityIds.Should().NotBeNull();
        entityIds!.Count.Should().Be(3);
    }

    // AddSameAsRelationshipAsync

    [Fact]
    public async Task AddSameAsRelationshipAsync_SendsCorrectCypher()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var repo = CreateWriteCapture(calls);
        await repo.AddSameAsRelationshipAsync("e1", "e2", 0.9, "exact");
        calls.Should().HaveCount(1);
        calls[0].Cypher.Should().Contain("MATCH (e1:Entity {id: $entityId1})");
        calls[0].Cypher.Should().Contain("MATCH (e2:Entity {id: $entityId2})");
        calls[0].Cypher.Should().Contain("MERGE (e1)-[r:SAME_AS]->(e2)");
        calls[0].Cypher.Should().Contain("r.confidence = $confidence");
        calls[0].Cypher.Should().Contain("r.match_type = $matchType");
    }

    [Fact]
    public async Task AddSameAsRelationshipAsync_PassesAllParameters()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var repo = CreateWriteCapture(calls);
        await repo.AddSameAsRelationshipAsync("entity-a", "entity-b", 0.92, "fuzzy");
        var param = calls[0].Parameters!;
        param.GetType().GetProperty("entityId1")!.GetValue(param).Should().Be("entity-a");
        param.GetType().GetProperty("entityId2")!.GetValue(param).Should().Be("entity-b");
        param.GetType().GetProperty("confidence")!.GetValue(param).Should().Be(0.92);
        param.GetType().GetProperty("matchType")!.GetValue(param).Should().Be("fuzzy");
    }

    // GetSameAsEntitiesAsync

    [Fact]
    public async Task GetSameAsEntitiesAsync_SendsCorrectCypher()
    {
        var (repo, calls) = CreateSameAsReadCapture();
        await repo.GetSameAsEntitiesAsync("ent-1");
        calls.Should().HaveCount(1);
        calls[0].Cypher.Should().Contain("-[r:SAME_AS]-");
        calls[0].Cypher.Should().Contain("r.confidence AS confidence");
        calls[0].Cypher.Should().Contain("r.match_type AS matchType");
    }

    [Fact]
    public async Task GetSameAsEntitiesAsync_ReturnsEmptyListWhenNoMatches()
    {
        var (repo, _) = CreateSameAsReadCapture();
        var result = await repo.GetSameAsEntitiesAsync("ent-unknown");
        result.Should().BeEmpty();
    }

    // MergeEntitiesAsync

    [Fact]
    public async Task MergeEntitiesAsync_SendsCorrectCypher()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var repo = CreateWriteCapture(calls);
        await repo.MergeEntitiesAsync("source-id", "target-id");
        calls.Should().HaveCountGreaterThanOrEqualTo(1);
        calls[0].Cypher.Should().Contain("MATCH (source:Entity {id: $sourceEntityId})");
        calls[0].Cypher.Should().Contain("MATCH (target:Entity {id: $targetEntityId})");
        calls[0].Cypher.Should().Contain("MERGE (m)-[:MENTIONS]->(target)");
        calls[0].Cypher.Should().Contain("source.merged_into = target.id");
        calls[0].Cypher.Should().Contain("target.aliases");
    }

    [Fact]
    public async Task MergeEntitiesAsync_TransfersSameAsRelationships()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var repo = CreateWriteCapture(calls);
        await repo.MergeEntitiesAsync("src", "tgt");
        calls[0].Cypher.Should().Contain("MERGE (target)-[:SAME_AS");
    }

    [Fact]
    public async Task MergeEntitiesAsync_PassesSourceAndTargetIds()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var repo = CreateWriteCapture(calls);
        await repo.MergeEntitiesAsync("source-entity", "target-entity");
        var param = calls[0].Parameters!;
        param.GetType().GetProperty("sourceEntityId")!.GetValue(param).Should().Be("source-entity");
        param.GetType().GetProperty("targetEntityId")!.GetValue(param).Should().Be("target-entity");
    }

    // Re-embedding after merge (G9)

    [Fact]
    public async Task MergeEntitiesAsync_ClearsTargetEmbedding()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var repo = CreateWriteCapture(calls);
        await repo.MergeEntitiesAsync("src", "tgt");
        calls[0].Cypher.Should().Contain("target.embedding = null");
    }

    [Fact]
    public async Task MergeEntitiesAsync_ClearsEmbeddingAfterAliasUpdate()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var repo = CreateWriteCapture(calls);
        await repo.MergeEntitiesAsync("src", "tgt");
        var cypher = calls[0].Cypher;
        var aliasIdx = cypher.IndexOf("target.aliases");
        var embeddingIdx = cypher.IndexOf("target.embedding = null");
        embeddingIdx.Should().BeGreaterThan(aliasIdx, "embedding should be cleared after alias merge");
    }
}
