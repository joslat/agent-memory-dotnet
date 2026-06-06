using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Repositories;
using Neo4j.Driver;
using Testcontainers.Neo4j;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// End-to-end proof of the R1b store-isolation tier (<see cref="MemoryStorageStrategy.DatabasePerApplication"/>):
/// the real <see cref="Neo4jMemoryStoreProvisioner"/> physically creates a Neo4j database per application,
/// the real <see cref="Neo4jSessionFactory"/> routes reads/writes to it from the ambient
/// <see cref="DefaultMemoryStoreContext"/>, and data written under one application is invisible to another.
///
/// Requires Neo4j <b>Enterprise</b> (multi-database / <c>CREATE DATABASE</c>), so this class manages its own
/// enterprise container instead of the shared Community fixture. It is the marquee coverage the audit
/// flagged as missing (store routing + provisioner were previously only mock-tested).
/// </summary>
[Trait("Category", "Integration")]
[Trait("Edition", "Enterprise")] // pulls neo4j:*-enterprise + accepts the eval license; filter with Edition!=Enterprise to skip
public sealed class StoreIsolationIntegrationTests : IAsyncLifetime
{
    private const string Username = "neo4j";
    private const string Password = "testpassword";
    private const int Dimensions = 4;

    private Neo4jContainer? _container;
    private IDriver _driver = null!;
    private DefaultMemoryStoreContext _storeContext = null!;
    private Neo4jSessionFactory _sessionFactory = null!;
    private Neo4jTransactionRunner _txRunner = null!;
    private Neo4jMemoryStoreProvisioner _provisioner = null!;
    private Neo4jConversationRepository _conversations = null!;

    public async Task InitializeAsync()
    {
        _container = new Neo4jBuilder("neo4j:5.26")
            .WithEnterpriseEdition(true) // multi-database support; accepts the eval license
            .WithEnvironment("NEO4J_AUTH", $"{Username}/{Password}")
            .Build();

        await _container.StartAsync();

        _driver = GraphDatabase.Driver(
            _container.GetConnectionString(), AuthTokens.Basic(Username, Password));

        var neo4jOptions = Options.Create(new Neo4jOptions
        {
            Database = "neo4j",
            EmbeddingDimensions = Dimensions,
        });
        var storeOptions = Options.Create(new MemoryStoreOptions
        {
            Strategy = MemoryStorageStrategy.DatabasePerApplication,
            AutoProvision = true,
            DatabasePrefix = "mem-",
        });

        _storeContext = new DefaultMemoryStoreContext();
        _sessionFactory = new Neo4jSessionFactory(
            new WrappingDriverFactory(_driver), neo4jOptions, storeOptions, _storeContext);
        _txRunner = new Neo4jTransactionRunner(_sessionFactory, NullLogger<Neo4jTransactionRunner>.Instance);
        _provisioner = new Neo4jMemoryStoreProvisioner(
            _sessionFactory, neo4jOptions, storeOptions, NullLogger<Neo4jMemoryStoreProvisioner>.Instance);
        _conversations = new Neo4jConversationRepository(
            _txRunner, NullLogger<Neo4jConversationRepository>.Instance);
    }

    public async Task DisposeAsync()
    {
        if (_driver is not null) await _driver.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }

    private async Task<string> WriteConversationInStoreAsync(string applicationId)
    {
        _storeContext.ApplicationId = applicationId;
        await _provisioner.EnsureStoreAsync(applicationId);

        var conv = new Conversation
        {
            ConversationId = $"conv-{Guid.NewGuid():N}",
            SessionId = $"session-{Guid.NewGuid():N}",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        await _conversations.UpsertAsync(conv);
        return conv.ConversationId;
    }

    [Fact]
    public async Task DatabasePerApplication_IsolatesDataAcrossStores()
    {
        var alphaConv = await WriteConversationInStoreAsync("app-alpha");
        var betaConv = await WriteConversationInStoreAsync("app-beta");

        // Reading under app-alpha sees only app-alpha's data.
        _storeContext.ApplicationId = "app-alpha";
        (await _conversations.GetByIdAsync(alphaConv)).Should().NotBeNull("written in app-alpha's store");
        (await _conversations.GetByIdAsync(betaConv)).Should().BeNull("app-beta's data lives in a different database");

        // Reading under app-beta sees only app-beta's data.
        _storeContext.ApplicationId = "app-beta";
        (await _conversations.GetByIdAsync(betaConv)).Should().NotBeNull("written in app-beta's store");
        (await _conversations.GetByIdAsync(alphaConv)).Should().BeNull("app-alpha's data lives in a different database");
    }

    [Fact]
    public async Task EnsureStoreAsync_PhysicallyCreatesTheStoreDatabase()
    {
        await _provisioner.EnsureStoreAsync("app-gamma");

        var exists = await DatabaseExistsAsync("mem-app-gamma");
        exists.Should().BeTrue("DatabasePerApplication provisioning must CREATE DATABASE for the store");
    }

    [Fact]
    public async Task EnsureStoreAsync_IsIdempotent()
    {
        await _provisioner.EnsureStoreAsync("app-delta");

        await FluentActions
            .Awaiting(() => _provisioner.EnsureStoreAsync("app-delta"))
            .Should().NotThrowAsync("re-provisioning an existing store is a safe no-op");

        (await DatabaseExistsAsync("mem-app-delta")).Should().BeTrue();
    }

    private async Task<bool> DatabaseExistsAsync(string database)
    {
        await using var session = _driver.AsyncSession(c => c.WithDatabase("system"));
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "SHOW DATABASES YIELD name WHERE name = $name RETURN count(*) AS c", new { name = database });
            var record = await cursor.SingleAsync();
            return global::Neo4j.Driver.ValueExtensions.As<long>(record["c"]) > 0;
        });
    }

    // Minimal INeo4jDriverFactory wrapping the test container's driver, so the production
    // Neo4jSessionFactory (which routes per ambient store context) can be exercised end-to-end.
    private sealed class WrappingDriverFactory : INeo4jDriverFactory
    {
        private readonly IDriver _driver;
        public WrappingDriverFactory(IDriver driver) => _driver = driver;
        public IDriver GetDriver() => _driver;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask; // container owns the driver
    }
}
