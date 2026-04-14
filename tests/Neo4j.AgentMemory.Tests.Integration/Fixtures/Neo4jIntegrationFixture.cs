using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;
using Testcontainers.Neo4j;

namespace Neo4j.AgentMemory.Tests.Integration.Fixtures;

/// <summary>
/// Shared xUnit fixture that starts a Neo4j Testcontainer, runs SchemaBootstrapper once,
/// and provides helpers for per-test database cleanup.
/// </summary>
public sealed class Neo4jIntegrationFixture : IAsyncLifetime
{
    private Neo4jContainer? _container;
    private IDriver? _driver;

    private const string ContainerUsername = "neo4j";
    private const string ContainerPassword = "testpassword";

    /// <summary>Embedding dimension used for all vector indexes in tests.</summary>
    public const int TestEmbeddingDimensions = 4;

    public INeo4jTransactionRunner TransactionRunner { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _container = new Neo4jBuilder("neo4j:5.26")
            .WithEnvironment("NEO4J_AUTH", $"{ContainerUsername}/{ContainerPassword}")
            .Build();

        await _container.StartAsync();

        _driver = GraphDatabase.Driver(
            _container.GetConnectionString(),
            AuthTokens.Basic(ContainerUsername, ContainerPassword));

        var options = Options.Create(new Neo4jOptions
        {
            Uri = _container.GetConnectionString(),
            Username = ContainerUsername,
            Password = ContainerPassword,
            Database = "neo4j",
            EmbeddingDimensions = TestEmbeddingDimensions
        });

        var sessionFactory = new DirectSessionFactory(_driver, "neo4j");
        TransactionRunner = new Neo4jTransactionRunner(
            sessionFactory,
            NullLogger<Neo4jTransactionRunner>.Instance);

        var bootstrapper = new SchemaBootstrapper(
            TransactionRunner,
            options,
            NullLogger<SchemaBootstrapper>.Instance);

        await bootstrapper.BootstrapAsync();
        await WaitForVectorIndexesAsync();
    }

    /// <summary>
    /// Removes all nodes and relationships. Call from each test class's InitializeAsync.
    /// </summary>
    public async Task CleanDatabaseAsync()
    {
        await using var session = _driver!.AsyncSession();
        await session.RunAsync("MATCH (n) DETACH DELETE n");
    }

    public async Task DisposeAsync()
    {
        if (_driver != null)
            await _driver.DisposeAsync();
        if (_container != null)
            await _container.DisposeAsync();
    }

    /// <summary>Polls until all VECTOR indexes are ONLINE or times out.</summary>
    private async Task WaitForVectorIndexesAsync(int timeoutSeconds = 60)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        while (!cts.IsCancellationRequested)
        {
            try
            {
                await using var session = _driver!.AsyncSession();
                var result = await session.RunAsync(
                    "SHOW INDEXES WHERE type = 'VECTOR' AND state <> 'ONLINE' RETURN count(*) AS pending");
                var record = await result.SingleAsync();
                var pending = global::Neo4j.Driver.ValueExtensions.As<long>(record["pending"]);
                if (pending == 0) return;
            }
            catch { /* ignore — Neo4j may not be fully ready */ }
            await Task.Delay(500);
        }
    }

    private sealed class DirectSessionFactory : INeo4jSessionFactory
    {
        private readonly IDriver _driver;
        private readonly string _database;

        public DirectSessionFactory(IDriver driver, string database)
        {
            _driver = driver;
            _database = database;
        }

        public IAsyncSession OpenSession(AccessMode accessMode = AccessMode.Write) =>
            _driver.AsyncSession(c => c
                .WithDatabase(_database)
                .WithDefaultAccessMode(accessMode));
    }
}
