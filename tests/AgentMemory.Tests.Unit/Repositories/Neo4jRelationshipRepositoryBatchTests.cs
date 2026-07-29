using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Repositories;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Repositories;

public sealed class Neo4jRelationshipRepositoryBatchTests
{
    [Fact]
    public async Task UpsertBatchAsync_EmptyList_DoesNotOpenTransaction()
    {
        var (repository, calls) = CreateCapture();

        var result = await repository.UpsertBatchAsync(Array.Empty<Relationship>());

        result.Should().BeEmpty();
        calls.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertBatchAsync_UsesOneUnwindWithOwnerAndTemporalProperties()
    {
        var (repository, calls) = CreateCapture();
        var relationships = new[]
        {
            Relationship("relationship-1", "entity-1", "entity-2"),
            Relationship("relationship-2", "entity-2", "entity-1")
        };

        await repository.UpsertBatchAsync(relationships);

        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("UNWIND $items AS item");
        calls[0].Cypher.Should().Contain("r.owner_id");
        calls[0].Cypher.Should().Contain("r.valid_from");
        calls[0].Cypher.Should().Contain("r.valid_until");

        var parameters = calls[0].Parameters!;
        var items = (IEnumerable<object>)parameters.GetType().GetProperty("items")!.GetValue(parameters)!;
        items.Cast<IDictionary<string, object?>>().Should().OnlyContain(item =>
            item.ContainsKey("owner_id") &&
            item.ContainsKey("source_message_ids") &&
            item.ContainsKey("metadata"));
    }

    private static Relationship Relationship(string id, string sourceId, string targetId) => new()
    {
        RelationshipId = id,
        SourceEntityId = sourceId,
        TargetEntityId = targetId,
        RelationshipType = "KNOWS",
        Confidence = 0.9,
        OwnerId = "owner-1",
        SourceMessageIds = ["message-1"],
        CreatedAtUtc = DateTimeOffset.Parse("2026-07-29T00:00:00Z")
    };

    private static (
        Neo4jRelationshipRepository Repository,
        List<(string Cypher, object? Parameters)> Calls) CreateCapture()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var transactionRunner = Substitute.For<INeo4jTransactionRunner>();
        transactionRunner
            .WriteAsync(
                Arg.Any<Func<IAsyncQueryRunner, Task<List<Relationship>>>>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<List<Relationship>>>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                runner.RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(info =>
                    {
                        calls.Add((info.Arg<string>(), info.ArgAt<object>(1)));
                        var cursor = Substitute.For<IResultCursor>();
                        cursor.FetchAsync().Returns(false);
                        return cursor;
                    });
                return await work(runner);
            });

        return (
            new Neo4jRelationshipRepository(
                transactionRunner,
                NullLogger<Neo4jRelationshipRepository>.Instance),
            calls);
    }
}
