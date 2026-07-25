using AgentMemory;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;
using Testcontainers.Neo4j;

namespace AgentMemory.Cli.Perf;

/// <summary>
/// The hermetic execution profile: a pinned Neo4j container plus deterministic stand-ins for the two
/// external services (embeddings and the model), wired into a real DI graph.
/// </summary>
/// <remarks>
/// <para>
/// "Hermetic" means every input is controlled: same image, same schema, same vectors, same model
/// responses, no network. That is what makes the structural counters reproducible enough to assert on.
/// It deliberately does <em>not</em> mean "fast at any cost" — the stand-ins inject latency on request
/// so the profile can also reproduce the shape of a remote deployment.
/// </para>
/// <para>
/// The service graph is the real one: <c>AddNeo4jAgentMemory</c> with LLM extraction opted in, exactly
/// as a consumer would configure it. Only the two leaf providers are substituted.
/// </para>
/// </remarks>
public sealed class HermeticProfile : IAsyncDisposable
{
    private const string ContainerUser = "neo4j";
    private const string ContainerPassword = "perfpassword";
    private const string Image = "neo4j:5.26";

    private Neo4jContainer? _container;
    private ServiceProvider? _provider;
    private AsyncServiceScope _scope;
    private bool _scopeCreated;

    private HermeticProfile(int dimensions) => Dimensions = dimensions;

    /// <summary>Embedding dimensionality. Small by design — vector width is not what is being measured.</summary>
    public int Dimensions { get; }

    /// <summary>Scoped service provider for resolving memory services.</summary>
    public IServiceProvider Services => _scope.ServiceProvider;

    /// <summary>Raw driver, for bulk fixture seeding that would be pointlessly slow through the services.</summary>
    public IDriver Driver { get; private set; } = null!;

    public static async Task<HermeticProfile> StartAsync(
        int dimensions,
        TimeSpan embeddingLatency,
        TimeSpan modelLatency,
        TextWriter log,
        CancellationToken cancellationToken = default)
    {
        var profile = new HermeticProfile(dimensions);
        await profile.InitializeAsync(embeddingLatency, modelLatency, log, cancellationToken).ConfigureAwait(false);
        return profile;
    }

    private async Task InitializeAsync(
        TimeSpan embeddingLatency, TimeSpan modelLatency, TextWriter log, CancellationToken cancellationToken)
    {
        log.WriteLine($"perf: starting {Image} (Testcontainers)…");
        _container = new Neo4jBuilder(Image)
            .WithEnvironment("NEO4J_AUTH", $"{ContainerUser}/{ContainerPassword}")
            .Build();
        await _container.StartAsync(cancellationToken).ConfigureAwait(false);

        var uri = _container.GetConnectionString();
        Driver = GraphDatabase.Driver(uri, AuthTokens.Basic(ContainerUser, ContainerPassword));

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

        services.AddNeo4jAgentMemory(
            memory => { /* shipped defaults — measuring anything else would measure a strawman */ },
            neo4j =>
            {
                neo4j.Uri = uri;
                neo4j.Username = ContainerUser;
                neo4j.Password = ContainerPassword;
                neo4j.Database = "neo4j";
                neo4j.EmbeddingDimensions = Dimensions;
            },
            // A non-null delegate is what opts the LLM extractors in; without it the Core no-op stubs
            // stay registered and a post-turn scenario would measure extraction that never happens.
            llm => { });

        // Counting wrappers sit outermost so they observe every call the product makes, including the
        // ones issued from inside the extraction pipeline.
        services.RemoveAll<IEmbeddingGenerator<string, Embedding<float>>>();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new CountingEmbeddingGenerator(
                new LatencyInjectingEmbeddingGenerator(
                    new DeterministicEmbeddingGenerator(Dimensions), embeddingLatency)));

        services.RemoveAll<IChatClient>();
        services.AddSingleton<IChatClient>(new CountingChatClient(new ScriptedChatClient(modelLatency)));

        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateAsyncScope();
        _scopeCreated = true;

        log.WriteLine("perf: bootstrapping schema…");
        await Services.GetRequiredService<ISchemaBootstrapper>()
            .BootstrapAsync(cancellationToken).ConfigureAwait(false);

        await using var session = Driver.AsyncSession();
        await session.RunAsync("CALL db.awaitIndexes(120)").ConfigureAwait(false);
        log.WriteLine("perf: schema ready.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_scopeCreated) await _scope.DisposeAsync().ConfigureAwait(false);
        if (_provider is not null) await _provider.DisposeAsync().ConfigureAwait(false);
        if (Driver is not null) await Driver.DisposeAsync().ConfigureAwait(false);
        if (_container is not null) await _container.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Adds a fixed delay to every embedding request so the hermetic profile can reproduce the latency
    /// shape of a remote provider without a network. Separate from the counting decorator so latency and
    /// accounting stay independently composable.
    /// </summary>
    private sealed class LatencyInjectingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly IEmbeddingGenerator<string, Embedding<float>> _inner;
        private readonly TimeSpan _delay;

        public LatencyInjectingEmbeddingGenerator(
            IEmbeddingGenerator<string, Embedding<float>> inner, TimeSpan delay)
        {
            _inner = inner;
            _delay = delay;
        }

        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (_delay > TimeSpan.Zero)
                await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
            return await _inner.GenerateAsync(values, options, cancellationToken).ConfigureAwait(false);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : _inner.GetService(serviceType, serviceKey);

        public void Dispose() => _inner.Dispose();
    }
}
