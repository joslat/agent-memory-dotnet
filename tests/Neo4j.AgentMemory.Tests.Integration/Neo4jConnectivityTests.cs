using FluentAssertions;
using Neo4j.Driver;

namespace Neo4j.AgentMemory.Tests.Integration;

[Collection("Neo4j")]
public class Neo4jConnectivityTests
{
    private readonly Neo4jTestFixture _fixture;

    public Neo4jConnectivityTests(Neo4jTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task CanConnectToNeo4j()
    {
        await using var driver = GraphDatabase.Driver(
            _fixture.ConnectionUri,
            AuthTokens.Basic(_fixture.Username, _fixture.Password));

        await using var session = driver.AsyncSession();
        var result = await session.RunAsync("RETURN 1 AS result");
        var record = await result.SingleAsync();

        global::Neo4j.Driver.ValueExtensions.As<long>(record["result"]).Should().Be(1L);
    }

    [Fact]
    public async Task CanCreateAndQueryNode()
    {
        var nodeId = $"smoke-{Guid.NewGuid():N}";

        await using var driver = GraphDatabase.Driver(
            _fixture.ConnectionUri,
            AuthTokens.Basic(_fixture.Username, _fixture.Password));

        await using var session = driver.AsyncSession();

        await session.RunAsync(
            "CREATE (n:SmokeTest {id: $id}) RETURN n",
            new { id = nodeId });

        var result = await session.RunAsync(
            "MATCH (n:SmokeTest {id: $id}) RETURN n.id AS nodeId",
            new { id = nodeId });

        var record = await result.SingleAsync();
        global::Neo4j.Driver.ValueExtensions.As<string>(record["nodeId"]).Should().Be(nodeId);

        // Cleanup
        await session.RunAsync(
            "MATCH (n:SmokeTest {id: $id}) DELETE n",
            new { id = nodeId });
    }
}
