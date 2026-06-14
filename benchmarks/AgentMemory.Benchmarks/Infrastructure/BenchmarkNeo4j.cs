using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;
using Testcontainers.Neo4j;

namespace AgentMemory.Benchmarks.Infrastructure;

/// <summary>
/// A single Neo4j Testcontainer shared by every benchmark in the process. BenchmarkDotNet is configured
/// to run in-process (see <see cref="BenchmarkConfig"/>), so a process-wide singleton is safe and avoids
/// paying container startup once per benchmark method. Started lazily on first <see cref="GetAsync"/> and
/// torn down by <c>Program</c> after the run completes.
/// </summary>
public sealed class BenchmarkNeo4j : IAsyncDisposable
{
    /// <summary>Embedding dimensionality used for the vector indexes and all seeded vectors.</summary>
    public const int EmbeddingDimensions = 768;

    private const string ContainerUsername = "neo4j";
    private const string ContainerPassword = "benchpassword";

    private Neo4jContainer _container = null!;
    private IDriver _driver = null!;

    public IDriver Driver => _driver;
    public INeo4jTransactionRunner TransactionRunner { get; private set; } = null!;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static BenchmarkNeo4j? _instance;

    /// <summary>Returns the shared instance, starting (and bootstrapping) the container on first call.</summary>
    public static async Task<BenchmarkNeo4j> GetAsync()
    {
        if (_instance is not null) return _instance;
        await Gate.WaitAsync();
        try
        {
            if (_instance is null)
            {
                var db = new BenchmarkNeo4j();
                await db.StartAsync();
                _instance = db;
            }
            return _instance;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Disposes the shared instance, if started. Call once from <c>Program</c> after the run.</summary>
    public static async Task ShutdownAsync()
    {
        if (_instance is not null)
        {
            await _instance.DisposeAsync();
            _instance = null;
        }
    }

    private async Task StartAsync()
    {
        _container = new Neo4jBuilder("neo4j:5.26")
            .WithEnvironment("NEO4J_AUTH", $"{ContainerUsername}/{ContainerPassword}")
            .Build();

        await _container.StartAsync();

        _driver = GraphDatabase.Driver(
            _container.GetConnectionString(),
            AuthTokens.Basic(ContainerUsername, ContainerPassword));

        var options = Microsoft.Extensions.Options.Options.Create(new Neo4jOptions
        {
            Uri = _container.GetConnectionString(),
            Username = ContainerUsername,
            Password = ContainerPassword,
            Database = "neo4j",
            EmbeddingDimensions = EmbeddingDimensions
        });

        var sessionFactory = new DirectSessionFactory(_driver, "neo4j");
        TransactionRunner = new Neo4jTransactionRunner(sessionFactory, NullLogger<Neo4jTransactionRunner>.Instance);

        var bootstrapper = new SchemaBootstrapper(TransactionRunner, options, NullLogger<SchemaBootstrapper>.Instance);
        await bootstrapper.BootstrapAsync();

        await using var session = _driver.AsyncSession();
        await session.RunAsync("CALL db.awaitIndexes(120)");
    }

    /// <summary>Deletes every node (and its relationships). Each benchmark's GlobalSetup starts from clean.</summary>
    public async Task CleanAsync()
    {
        await using var session = _driver.AsyncSession();
        await session.RunAsync("MATCH (n) DETACH DELETE n");
    }

    /// <summary>
    /// Seeds <paramref name="count"/> <c>:Entity</c> nodes carrying deterministic embeddings, so the
    /// entity vector index is populated for the vector-search benchmark. Written in chunks via UNWIND.
    /// </summary>
    public async Task SeedEntitiesWithEmbeddingsAsync(int count, int chunkSize = 500)
    {
        const string cypher = @"
            UNWIND $items AS item
            CREATE (e:Entity {
                id: item.id, name: item.name, type: 'CONCEPT', confidence: 0.9,
                created_at: datetime(item.createdAt), embedding: item.embedding
            })";

        await using var session = _driver.AsyncSession();
        var createdAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToString("O");

        for (var start = 0; start < count; start += chunkSize)
        {
            var end = Math.Min(start + chunkSize, count);
            var items = new List<object>(end - start);
            for (var i = start; i < end; i++)
            {
                items.Add(new
                {
                    id = $"bench-entity-{i}",
                    name = $"entity {i}",
                    createdAt,
                    embedding = DeterministicEmbedding.Create(i, EmbeddingDimensions)
                });
            }
            await session.RunAsync(cypher, new { items });
        }
    }

    /// <summary>
    /// Seeds <paramref name="count"/> fresh, high-confidence <c>:Entity</c> nodes for the decay benchmark.
    /// Recent <c>created_at</c> + confidence 0.95 ⇒ retention score ≈ 0.95, well above the default 0.1
    /// threshold, so a prune pass scans+scores them all and removes none (a repeatable steady-state run).
    /// </summary>
    public async Task SeedFreshEntitiesAsync(int count, int chunkSize = 1000)
    {
        const string cypher = @"
            UNWIND $items AS item
            CREATE (e:Entity {
                id: item.id, name: item.name, type: 'CONCEPT', confidence: 0.95,
                created_at: datetime($now), last_accessed_at: datetime($now), access_count: 1
            })";

        await using var session = _driver.AsyncSession();
        var now = DateTimeOffset.UtcNow.ToString("O");

        for (var start = 0; start < count; start += chunkSize)
        {
            var end = Math.Min(start + chunkSize, count);
            var items = new List<object>(end - start);
            for (var i = start; i < end; i++)
                items.Add(new { id = $"bench-decay-{i}", name = $"entity {i}" });
            await session.RunAsync(cypher, new { items, now });
        }
    }

    /// <summary>Creates the <c>:Knowledge</c> vector + fulltext indexes used by the hybrid-retrieval benchmark.</summary>
    public async Task CreateKnowledgeIndexesAsync()
    {
        await using var session = _driver.AsyncSession();
        await session.RunAsync(
            $"CREATE VECTOR INDEX knowledge_vec_idx IF NOT EXISTS FOR (n:Knowledge) ON (n.embedding) " +
            $"OPTIONS {{indexConfig: {{`vector.dimensions`: {EmbeddingDimensions}, `vector.similarity_function`: 'cosine'}}}}");
        await session.RunAsync(
            "CREATE FULLTEXT INDEX knowledge_ft_idx IF NOT EXISTS FOR (n:Knowledge) ON EACH [n.text]");
        await session.RunAsync("CALL db.awaitIndexes(120)");
    }

    /// <summary>Seeds <paramref name="count"/> <c>:Knowledge</c> nodes with text + deterministic embeddings.</summary>
    public async Task SeedKnowledgeAsync(int count, int chunkSize = 500)
    {
        const string cypher = @"
            UNWIND $items AS item
            CREATE (k:Knowledge {id: item.id, text: item.text, embedding: item.embedding})";

        await using var session = _driver.AsyncSession();
        for (var start = 0; start < count; start += chunkSize)
        {
            var end = Math.Min(start + chunkSize, count);
            var items = new List<object>(end - start);
            for (var i = start; i < end; i++)
            {
                items.Add(new
                {
                    id = $"bench-knowledge-{i}",
                    text = $"knowledge node {i} about alpha beta gamma topic {i % 50}",
                    embedding = DeterministicEmbedding.Create(i, EmbeddingDimensions)
                });
            }
            await session.RunAsync(cypher, new { items });
        }
    }

    /// <summary>Builds an in-memory list of <see cref="Entity"/> for the batch-upsert benchmark (no embeddings).</summary>
    public static IReadOnlyList<Entity> BuildEntities(int count)
    {
        var createdAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var list = new List<Entity>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add(new Entity
            {
                EntityId = $"bench-upsert-{i}",
                Name = $"entity {i}",
                Type = "CONCEPT",
                Confidence = 0.9,
                CreatedAtUtc = createdAt
            });
        }
        return list;
    }

    public async ValueTask DisposeAsync()
    {
        if (_driver is not null) await _driver.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
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
            _driver.AsyncSession(c => c.WithDatabase(database).WithDefaultAccessMode(accessMode));
    }
}
