using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Neo4j.Services;
using AgentMemory.Tests.Integration.Fixtures;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Services;

/// <summary>
/// R5 #11 — end-to-end proof that <see cref="Neo4jGraphQueryService"/> is read-only. The MCP
/// <c>graph_query</c> tool forwards arbitrary Cypher; its sole mutation guard is that the service runs
/// every statement inside a Neo4j <em>read</em> transaction (via <c>INeo4jTransactionRunner.ReadAsync</c>),
/// which the server rejects writes against. These tests fire genuinely destructive Cypher at a live
/// container and assert (a) it throws a client error and (b) the graph is left untouched. The dispatch
/// half of the invariant (read path is always chosen, even for write-shaped text) is pinned by the unit
/// test <c>Neo4jGraphQueryServiceTests.QueryAsync_AlwaysDispatchesToReadTransaction_NeverWrite</c>.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class GraphQueryReadOnlyIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jGraphQueryService _sut;

    public GraphQueryReadOnlyIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _sut = new Neo4jGraphQueryService(
            fixture.TransactionRunner, NullLogger<Neo4jGraphQueryService>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData("CREATE (n:GraphQueryReadOnlyProbe {marker: 'r5-11'}) RETURN n")]
    [InlineData("MERGE (n:GraphQueryReadOnlyProbe {marker: 'r5-11'}) SET n.hacked = true RETURN n")]
    [InlineData("MATCH (n) DETACH DELETE n")]
    public async Task QueryAsync_WriteCypher_IsRejectedByReadTransaction(string writeCypher)
    {
        // Seed one node so a successful DETACH DELETE would be observable as data loss.
        await SeedProbeNodeAsync();

        var act = async () => await _sut.QueryAsync(writeCypher);

        // Neo4j raises a ClientError (ForbiddenAccessMode / write-in-read-tx) which the driver surfaces
        // as a Neo4jException. The exact subtype/message varies by version, so assert on the base type.
        await act.Should().ThrowAsync<Neo4jException>();

        // The graph is unchanged: the seed node still exists and nothing was created/mutated.
        var count = await CountProbeNodesAsync();
        count.Should().Be(1, "a rejected write transaction must not have altered the graph");
    }

    [Fact]
    public async Task QueryAsync_ReadCypher_StillSucceeds()
    {
        await SeedProbeNodeAsync();

        var rows = await _sut.QueryAsync(
            "MATCH (n:GraphQueryReadOnlyProbe) RETURN n.marker AS marker");

        rows.Should().ContainSingle();
        rows[0]["marker"].Should().Be("r5-11");
    }

    private async Task SeedProbeNodeAsync()
    {
        await using var session = _fixture.Driver.AsyncSession();
        await session.RunAsync("CREATE (n:GraphQueryReadOnlyProbe {marker: 'r5-11'})");
    }

    private async Task<long> CountProbeNodesAsync()
    {
        await using var session = _fixture.Driver.AsyncSession();
        var cursor = await session.RunAsync(
            "MATCH (n:GraphQueryReadOnlyProbe) RETURN count(n) AS c");
        var record = await cursor.SingleAsync();
        return global::Neo4j.Driver.ValueExtensions.As<long>(record["c"]);
    }
}
