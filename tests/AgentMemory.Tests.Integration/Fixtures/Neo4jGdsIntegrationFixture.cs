using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;
using Testcontainers.Neo4j;
using Xunit;

namespace AgentMemory.Tests.Integration.Fixtures;

/// <summary>
/// xUnit fixture that starts a Neo4j Testcontainer with the <b>Graph Data Science (GDS)</b> plugin loaded
/// (<c>NEO4J_PLUGINS=["graph-data-science"]</c>), so the optional <c>AgentMemory.Analytics</c> services can
/// be validated against real GDS algorithms. Separate from <see cref="Neo4jIntegrationFixture"/> because the
/// plugin download makes startup slower; analytics tests don't need the vector/fulltext schema.
/// </summary>
public sealed class Neo4jGdsIntegrationFixture : IAsyncLifetime
{
    private Neo4jContainer? _container;
    private IDriver? _driver;

    private const string User = "neo4j";
    private const string Password = "testpassword";

    public INeo4jTransactionRunner TransactionRunner { get; private set; } = null!;
    public IDriver Driver => _driver!;

    public async Task InitializeAsync()
    {
        _container = new Neo4jBuilder("neo4j:5.26")
            .WithEnvironment("NEO4J_AUTH", $"{User}/{Password}")
            .WithEnvironment("NEO4J_PLUGINS", "[\"graph-data-science\"]")
            .WithEnvironment("NEO4J_dbms_security_procedures_unrestricted", "gds.*")
            .WithEnvironment("NEO4J_dbms_security_procedures_allowlist", "gds.*")
            .Build();

        await _container.StartAsync();

        _driver = GraphDatabase.Driver(
            _container.GetConnectionString(), AuthTokens.Basic(User, Password));

        TransactionRunner = new Neo4jTransactionRunner(
            new DirectSessionFactory(_driver, "neo4j"),
            NullLogger<Neo4jTransactionRunner>.Instance);

        await WaitForGdsAsync();
    }

    /// <summary>Removes all nodes and relationships. Call from each test class's InitializeAsync.</summary>
    public async Task CleanDatabaseAsync()
    {
        await using var session = _driver!.AsyncSession();
        await session.RunAsync("MATCH (n) DETACH DELETE n");
    }

    public async Task DisposeAsync()
    {
        if (_driver != null) await _driver.DisposeAsync();
        if (_container != null) await _container.DisposeAsync();
    }

    /// <summary>Polls until <c>gds.version()</c> is callable (the plugin downloads on first boot).</summary>
    private async Task WaitForGdsAsync(int timeoutSeconds = 120)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        while (!cts.IsCancellationRequested)
        {
            try
            {
                await using var session = _driver!.AsyncSession();
                var result = await session.RunAsync("RETURN gds.version() AS v");
                await result.SingleAsync();
                return;
            }
            catch { /* GDS not ready yet */ }
            await Task.Delay(1000);
        }
        throw new InvalidOperationException("GDS plugin did not become available within the timeout.");
    }

    private sealed class DirectSessionFactory : INeo4jSessionFactory
    {
        private readonly IDriver _driver;
        private readonly string _database;
        public DirectSessionFactory(IDriver driver, string database) { _driver = driver; _database = database; }
        public IAsyncSession OpenSession(AccessMode accessMode = AccessMode.Write) => OpenSession(_database, accessMode);
        public IAsyncSession OpenSession(string database, AccessMode accessMode = AccessMode.Write) =>
            _driver.AsyncSession(c => c.WithDatabase(database).WithDefaultAccessMode(accessMode));
    }
}

/// <summary>Collection wrapper so the GDS fixture is shared across analytics integration test classes.</summary>
[CollectionDefinition("Neo4j GDS Integration")]
public sealed class Neo4jGdsCollection : ICollectionFixture<Neo4jGdsIntegrationFixture>;
