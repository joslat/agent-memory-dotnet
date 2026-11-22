using Neo4j.Driver;
using Testcontainers.Neo4j;

namespace AgentMemory.Tests.Integration.Fixtures;

/// <summary>
/// A Neo4j of its own for the Identity Kinds Benchmark (IKB-1). Its baseline was recorded on a fresh store whose vector
/// indexes hold 1024 dimensions, the token-hash embedder's; the shared fixture's store is built for 4, so the cases
/// cannot run there and still be the same run. Each case writes under an owner of its own, so the arms share the store.
/// </summary>
public sealed class IdentityKindsNeo4jFixture : IAsyncLifetime
{
    private Neo4jContainer? _container;

    public const string User = "neo4j";
    public const string Password = "identitykinds";

    public string Uri => _container!.GetConnectionString();

    public IDriver Driver { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var image = Environment.GetEnvironmentVariable("AGENTMEMORY_TEST_NEO4J_IMAGE") is { Length: > 0 } configured
            ? configured
            : "neo4j:5.26";
        _container = new Neo4jBuilder(image)
            .WithLabel("com.agentmemory.repository", "agent-memory-dotnet")
            .WithLabel("com.agentmemory.fixture", "IdentityKindsNeo4jFixture")
            .WithEnvironment("NEO4J_AUTH", $"{User}/{Password}")
            .WithEnvironment("NEO4J_ACCEPT_LICENSE_AGREEMENT", "yes")
            .Build();
        await _container.StartAsync();
        Driver = GraphDatabase.Driver(Uri, AuthTokens.Basic(User, Password));
    }

    public async Task DisposeAsync()
    {
        if (Driver is not null) await Driver.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }
}

// Serialized against every other collection, like the shared store's: two Neo4j containers busy at once would slow both.
[CollectionDefinition("Identity Kinds Neo4j", DisableParallelization = true)]
public sealed class IdentityKindsNeo4jCollection : ICollectionFixture<IdentityKindsNeo4jFixture>
{
}
