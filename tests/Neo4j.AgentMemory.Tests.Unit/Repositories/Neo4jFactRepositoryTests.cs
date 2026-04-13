using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.AgentMemory.Neo4j.Repositories;
using Neo4j.Driver;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Repositories;

public sealed class Neo4jFactRepositoryTests
{
    private static (Neo4jFactRepository Repo, List<(string Cypher, object? Parameters)> Calls)
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
        return (new Neo4jFactRepository(txRunner, NullLogger<Neo4jFactRepository>.Instance), calls);
    }

    private static (Neo4jFactRepository Repo, List<(string Cypher, object? Parameters)> Calls)
        CreateFactBatchWriteCapture()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<List<Fact>>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<List<Fact>>>>();
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
        return (new Neo4jFactRepository(txRunner, NullLogger<Neo4jFactRepository>.Instance), calls);
    }

    // ── CreateAboutRelationshipAsync ──

    [Fact]
    public async Task CreateAboutRelationshipAsync_SendsCorrectCypher()
    {
        var (repo, calls) = CreateWriteCapture();

        await repo.CreateAboutRelationshipAsync("f-1", "ent-1");

        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("MERGE (f)-[:ABOUT]->(e)");
    }

    [Fact]
    public async Task CreateAboutRelationshipAsync_PassesCorrectParameters()
    {
        var (repo, calls) = CreateWriteCapture();

        await repo.CreateAboutRelationshipAsync("f-10", "ent-20");

        var parameters = calls[0].Parameters!;
        parameters.GetType().GetProperty("factId")!.GetValue(parameters).Should().Be("f-10");
        parameters.GetType().GetProperty("entityId")!.GetValue(parameters).Should().Be("ent-20");
    }

    // ── CreateExtractedFromRelationshipAsync ──

    [Fact]
    public async Task CreateExtractedFromRelationshipAsync_SendsCorrectCypher()
    {
        var (repo, calls) = CreateWriteCapture();

        await repo.CreateExtractedFromRelationshipAsync("f-1", "msg-1");

        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("MERGE (f)-[:EXTRACTED_FROM]->(m)");
    }

    [Fact]
    public async Task CreateExtractedFromRelationshipAsync_PassesCorrectParameters()
    {
        var (repo, calls) = CreateWriteCapture();

        await repo.CreateExtractedFromRelationshipAsync("f-5", "msg-7");

        var parameters = calls[0].Parameters!;
        parameters.GetType().GetProperty("factId")!.GetValue(parameters).Should().Be("f-5");
        parameters.GetType().GetProperty("messageId")!.GetValue(parameters).Should().Be("msg-7");
    }

    // ── UpsertBatchAsync ──

    [Fact]
    public async Task UpsertBatchAsync_EmptyList_ReturnsEmpty()
    {
        var (repo, calls) = CreateFactBatchWriteCapture();

        var result = await repo.UpsertBatchAsync(Array.Empty<Fact>());

        result.Should().BeEmpty();
        calls.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertBatchAsync_SendsUnwindCypher()
    {
        var (repo, calls) = CreateFactBatchWriteCapture();
        var facts = new List<Fact>
        {
            new()
            {
                FactId = "f1", Subject = "Alice", Predicate = "works_at", Object = "Neo4j",
                Confidence = 0.9, SourceMessageIds = Array.Empty<string>(),
                CreatedAtUtc = DateTimeOffset.UtcNow
            }
        };

        await repo.UpsertBatchAsync(facts);

        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("UNWIND $items AS item");
    }

    [Fact]
    public async Task UpsertBatchAsync_SendsExtractedFromCypherForFactsWithSourceMessages()
    {
        var (repo, calls) = CreateFactBatchWriteCapture();
        var facts = new List<Fact>
        {
            new()
            {
                FactId = "f1", Subject = "Alice", Predicate = "works_at", Object = "Neo4j",
                Confidence = 0.9, SourceMessageIds = new[] { "msg-1" },
                CreatedAtUtc = DateTimeOffset.UtcNow
            }
        };

        await repo.UpsertBatchAsync(facts);

        calls.Should().HaveCount(2);
        calls[1].Cypher.Should().Contain("EXTRACTED_FROM");
    }

    [Fact]
    public async Task UpsertBatchAsync_SkipsExtractedFromForFactsWithoutSourceMessages()
    {
        var (repo, calls) = CreateFactBatchWriteCapture();
        var facts = new List<Fact>
        {
            new()
            {
                FactId = "f1", Subject = "Alice", Predicate = "works_at", Object = "Neo4j",
                Confidence = 0.9, SourceMessageIds = Array.Empty<string>(),
                CreatedAtUtc = DateTimeOffset.UtcNow
            }
        };

        await repo.UpsertBatchAsync(facts);

        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("UNWIND");
    }
}
