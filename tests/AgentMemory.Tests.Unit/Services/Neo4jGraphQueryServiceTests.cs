using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Services;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

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

    // ── read-only enforcement (R5 #11) ──
    // graph_query exposes arbitrary Cypher. Its only safety net against mutation is that the service
    // dispatches through INeo4jTransactionRunner.ReadAsync — a Neo4j *read* transaction, which the server
    // rejects writes against. These tests pin that invariant at the unit level: even write-shaped Cypher
    // is routed to the read path (never WriteAsync), so the read-only guarantee cannot be silently lost by
    // a future refactor that swaps the dispatch. The server-side rejection itself is proven by the
    // companion integration test (GraphQueryReadOnlyIntegrationTests).

    [Theory]
    [InlineData("MATCH (n:Entity) RETURN n LIMIT 10")]   // genuine read
    [InlineData("CREATE (n:Pwned {x: 1}) RETURN n")]      // write-shaped — must still go to the read tx
    [InlineData("MATCH (n) DETACH DELETE n")]             // destructive — must still go to the read tx
    [InlineData("MERGE (n:Entity {id: 'x'}) SET n.hacked = true")]
    public async Task QueryAsync_AlwaysDispatchesToReadTransaction_NeverWrite(string cypher)
    {
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .ReadAsync(
                Arg.Any<Func<IAsyncQueryRunner, Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                // Do NOT invoke the work delegate — a real read tx would reject the write before any rows
                // are produced. We only need to confirm which transaction kind the service chose.
                return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(
                    new List<IReadOnlyDictionary<string, object?>>());
            });

        var sut = new Neo4jGraphQueryService(txRunner, NullLogger<Neo4jGraphQueryService>.Instance);

        await sut.QueryAsync(cypher);

        await txRunner.Received(1).ReadAsync(
            Arg.Any<Func<IAsyncQueryRunner, Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>>>>(),
            Arg.Any<CancellationToken>());
        // The write overloads must never be touched, regardless of what the Cypher text looks like.
        await txRunner.DidNotReceive().WriteAsync(
            Arg.Any<Func<IAsyncQueryRunner, Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>>>>(),
            Arg.Any<CancellationToken>());
        await txRunner.DidNotReceive().WriteAsync(
            Arg.Any<Func<IAsyncQueryRunner, Task>>(),
            Arg.Any<CancellationToken>());
    }
}
