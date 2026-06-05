using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;
using Testcontainers.Neo4j;

namespace AgentMemory.Tests.Performance;

/// <summary>
/// Class fixture that starts a single Neo4j Testcontainer for the performance smoke tests,
/// bootstraps the schema once, and exposes the driver + transaction runner. Embeddings are not
/// exercised by these tests, so a small vector dimension is used purely to keep bootstrap cheap.
/// </summary>
public sealed class PerfNeo4jFixture : IAsyncLifetime
{
    private Neo4jContainer? _container;
    private IDriver? _driver;

    private const string ContainerUsername = "neo4j";
    private const string ContainerPassword = "testpassword";

    public INeo4jTransactionRunner TransactionRunner { get; private set; } = null!;
    public IDriver Driver => _driver!;

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
            EmbeddingDimensions = 4
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

        await using var session = _driver.AsyncSession();
        await session.RunAsync("CALL db.awaitIndexes(60)");
    }

    public async Task DisposeAsync()
    {
        if (_driver != null)
            await _driver.DisposeAsync();
        if (_container != null)
            await _container.DisposeAsync();
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
            OpenSession(_database, accessMode);

        public IAsyncSession OpenSession(string database, AccessMode accessMode = AccessMode.Write) =>
            _driver.AsyncSession(c => c
                .WithDatabase(database)
                .WithDefaultAccessMode(accessMode));
    }
}
