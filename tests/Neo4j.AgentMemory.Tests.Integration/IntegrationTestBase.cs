using Neo4j.Driver;

namespace Neo4j.AgentMemory.Tests.Integration;

[Collection("Neo4j")]
public abstract class IntegrationTestBase
{
    protected Neo4jTestFixture Fixture { get; }

    protected IntegrationTestBase(Neo4jTestFixture fixture)
    {
        Fixture = fixture;
    }

    /// <summary>
    /// Creates a Neo4j driver configured against the test container.
    /// Caller is responsible for disposing the driver.
    /// </summary>
    protected IDriver CreateDriver() =>
        GraphDatabase.Driver(
            Fixture.ConnectionUri,
            AuthTokens.Basic(Fixture.Username, Fixture.Password));

    /// <summary>
    /// Runs a Cypher query and discards the result. Useful for test setup/teardown.
    /// </summary>
    protected async Task RunCypherAsync(string cypher, object? parameters = null)
    {
        await using var driver = CreateDriver();
        await using var session = driver.AsyncSession();
        await session.RunAsync(cypher, parameters);
    }
}
