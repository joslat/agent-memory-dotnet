using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;
using Testcontainers.Neo4j;

namespace AgentMemory.Tests.Integration.Fixtures;

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

    /// <summary>Bolt connection string for the running container (for tests that build their own provider).</summary>
    public string ConnectionString => _container!.GetConnectionString();

    /// <summary>Container username (for tests that build their own provider/driver).</summary>
    public string User => ContainerUsername;

    /// <summary>Container password (for tests that build their own provider/driver).</summary>
    public string Password => ContainerPassword;

    /// <summary>The raw driver, for tests that construct services needing <see cref="IDriver"/> directly
    /// (e.g. the GraphRAG retrievers).</summary>
    public IDriver Driver => _driver!;

    /// <summary>
    /// The server's major version (5 for 5.26, 2026 for 2026.02). Some measurements are specific to one index
    /// implementation: 5.26's global vector index starves a crowded owner, 2026.x's does not.
    /// </summary>
    public async Task<int> ServerMajorAsync()
    {
        await using var session = Driver.AsyncSession();
        var cursor = await session.RunAsync("CALL dbms.components() YIELD name, versions WHERE name = 'Neo4j Kernel' RETURN versions[0] AS v");
        var version = global::Neo4j.Driver.ValueExtensions.As<string>((await cursor.SingleAsync())["v"]);
        return int.Parse(version.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task InitializeAsync()
    {
        // G-16: the server under test. 5.26 by default (what CI and most deployments run); set
        // AGENTMEMORY_TEST_NEO4J_IMAGE (e.g. neo4j:2026.02) to run the whole suite against a newer server.
        var image = Environment.GetEnvironmentVariable("AGENTMEMORY_TEST_NEO4J_IMAGE") is { Length: > 0 } configured
            ? configured
            : "neo4j:5.26";
        _container = new Neo4jBuilder(image)
            .WithEnvironment("NEO4J_AUTH", $"{ContainerUsername}/{ContainerPassword}")
            .WithEnvironment("NEO4J_ACCEPT_LICENSE_AGREEMENT", "yes")
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
        var token = cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var session = _driver!.AsyncSession();
                // SHOW INDEXES needs an explicit YIELD before WHERE/RETURN can reference its columns;
                // "SHOW INDEXES WHERE ... RETURN ..." is a Neo4j 5.x syntax error the catch would swallow,
                // making this poll silently burn the full timeout instead of returning once indexes are online.
                var result = await session.RunAsync(
                    "SHOW INDEXES YIELD type, state WHERE type = 'VECTOR' AND state <> 'ONLINE' RETURN count(*) AS pending");
                // Pass the bounded token so a stalled driver call cannot run past the timeout.
                var record = await result.SingleAsync(token);
                if (global::Neo4j.Driver.ValueExtensions.As<long>(record["pending"]) == 0) return;
            }
            catch (OperationCanceledException) { return; /* timed out — best-effort, proceed */ }
            catch { /* ignore — Neo4j may not be fully ready */ }
            try { await Task.Delay(500, token); }
            catch (OperationCanceledException) { return; }
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
            OpenSession(_database, accessMode);

        public IAsyncSession OpenSession(string database, AccessMode accessMode = AccessMode.Write) =>
            _driver.AsyncSession(c => c
                .WithDatabase(database)
                .WithDefaultAccessMode(accessMode));
    }
}
