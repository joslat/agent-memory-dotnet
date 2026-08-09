using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Repositories;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Repositories;

public sealed class Neo4jPreferenceRepositoryBatchTests
{
    [Fact]
    public async Task UpsertBatchAsync_EmptyList_DoesNotOpenTransaction()
    {
        var (repository, calls) = CreateCapture();

        var result = await repository.UpsertBatchAsync(Array.Empty<Preference>());

        result.Should().BeEmpty();
        calls.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertBatchAsync_UsesUnwindAndPreservesEmbeddingAndProvenanceWrites()
    {
        var (repository, calls) = CreateCapture();
        var preferences = new[]
        {
            Preference("preference-1", "coffee"),
            Preference("preference-2", "tea")
        };

        await repository.UpsertBatchAsync(preferences);

        calls.Should().HaveCount(5);
        calls[0].Cypher.Should().Contain("UNWIND $items AS item");
        calls.Count(call => call.Cypher.Contains("SET p.embedding")).Should().Be(2);
        calls.Count(call => call.Cypher.Contains("EXTRACTED_FROM")).Should().Be(2);

        var parameters = calls[0].Parameters!;
        var items = (IEnumerable<object>)parameters.GetType().GetProperty("items")!.GetValue(parameters)!;
        items.Cast<IDictionary<string, object?>>().Should().OnlyContain(item =>
            item.ContainsKey("owner_id") &&
            item.ContainsKey("source_message_ids") &&
            item.ContainsKey("metadata"));
    }

    private static Preference Preference(string id, string text) => new()
    {
        PreferenceId = id,
        Category = "drink",
        PreferenceText = text,
        Confidence = 0.9,
        Embedding = [0.1f, 0.2f],
        OwnerId = "owner-1",
        SourceMessageIds = ["message-1"],
        CreatedAtUtc = DateTimeOffset.Parse("2026-07-29T00:00:00Z")
    };

    private static (
        Neo4jPreferenceRepository Repository,
        List<(string Cypher, object? Parameters)> Calls) CreateCapture()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var transactionRunner = Substitute.For<INeo4jTransactionRunner>();
        transactionRunner
            .WriteAsync(
                Arg.Any<Func<IAsyncQueryRunner, Task<List<Preference>>>>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<List<Preference>>>>();
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
            new Neo4jPreferenceRepository(
                transactionRunner,
                NullLogger<Neo4jPreferenceRepository>.Instance),
            calls);
    }
}
