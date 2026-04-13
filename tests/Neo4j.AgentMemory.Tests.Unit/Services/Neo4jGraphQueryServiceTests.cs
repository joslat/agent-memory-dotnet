using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.AgentMemory.Neo4j.Services;
using Neo4j.Driver;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Services;

public sealed class Neo4jGraphQueryServiceTests
{
    private static (Neo4jGraphQueryService Sut, List<(string Cypher, Dictionary<string, object?> Params)> Calls)
        CreateCaptureSetup()
    {
        var calls = new List<(string Cypher, Dictionary<string, object?> Params)>();
        var txRunner = Substitute.For<INeo4jTransactionRunner>();

        txRunner
            .ReadAsync(
                Arg.Any<Func<IAsyncQueryRunner, Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>>>>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>>>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                var cursor = Substitute.For<IResultCursor>();
                cursor.FetchAsync().Returns(Task.FromResult(false));
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object?>>())
                    .Returns(ci =>
                    {
                        calls.Add((ci.Arg<string>(), ci.Arg<Dictionary<string, object?>>()));
                        return Task.FromResult(cursor);
                    });
                return await work(runner);
            });

        var sut = new Neo4jGraphQueryService(txRunner, NullLogger<Neo4jGraphQueryService>.Instance);
        return (sut, calls);
    }

    [Fact]
    public async Task QueryAsync_ForwardsCypherToTransactionRunner()
    {
        var (sut, calls) = CreateCaptureSetup();
        const string cypher = "MATCH (n:Entity) RETURN n LIMIT 10";

        await sut.QueryAsync(cypher);

        calls.Should().HaveCount(1);
        calls[0].Cypher.Should().Be(cypher);
    }

    [Fact]
    public async Task QueryAsync_WithNullParameters_PassesEmptyDictionary()
    {
        var (sut, calls) = CreateCaptureSetup();

        await sut.QueryAsync("MATCH (n) RETURN n", parameters: null);

        calls.Should().HaveCount(1);
        calls[0].Params.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryAsync_WithParameters_ForwardsAllEntriesCorrectly()
    {
        var (sut, calls) = CreateCaptureSetup();
        var parameters = new Dictionary<string, object?>
        {
            ["name"] = "Alice",
            ["type"] = "PERSON"
        };

        await sut.QueryAsync("MATCH (e:Entity {name: $name}) RETURN e", parameters);

        calls.Should().HaveCount(1);
        calls[0].Params.Should().ContainKey("name").WhoseValue.Should().Be("Alice");
        calls[0].Params.Should().ContainKey("type").WhoseValue.Should().Be("PERSON");
    }

    [Fact]
    public async Task QueryAsync_EmptyResultSet_ReturnsEmptyList()
    {
        var (sut, _) = CreateCaptureSetup();

        var result = await sut.QueryAsync("MATCH (n) RETURN n");

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryAsync_WithParametersContainingNullValue_IncludesNullEntry()
    {
        var (sut, calls) = CreateCaptureSetup();
        var parameters = new Dictionary<string, object?> { ["optionalField"] = null };

        await sut.QueryAsync("MATCH (n) WHERE n.field = $optionalField RETURN n", parameters);

        calls.Should().HaveCount(1);
        calls[0].Params.Should().ContainKey("optionalField");
        calls[0].Params["optionalField"].Should().BeNull();
    }
}
