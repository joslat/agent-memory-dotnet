using Testcontainers.Neo4j;

namespace Neo4j.AgentMemory.Tests.Integration;

public class Neo4jTestFixture : IAsyncLifetime
{
    private Neo4jContainer? _container;

    public string ConnectionUri => _container!.GetConnectionString();
    public string Username => "neo4j";
    public string Password => "testpassword";

    public async Task InitializeAsync()
    {
        _container = new Neo4jBuilder("neo4j:5.26")
            .WithEnvironment("NEO4J_AUTH", $"neo4j/{Password}")
            .Build();

        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container != null)
            await _container.DisposeAsync();
    }
}
