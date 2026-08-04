using AgentMemory;
using AgentMemory.Abstractions.Options;
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
    private readonly ScaleMRunVolume? _scaleRunVolume;
    private AsyncServiceScope _scope;
    private bool _scopeCreated;

    private HermeticProfile(
        int dimensions,
        PerfScale scale,
        ScaleMRunVolume? scaleRunVolume,
        int maxConnectionPoolSize,
        bool useBatchEntityResolutionSnapshots,
        bool useCoalescedPersistenceTransactions)
    {
        Dimensions = dimensions;
        Scale = scale;
        _scaleRunVolume = scaleRunVolume;
        MaxConnectionPoolSize = maxConnectionPoolSize;
        UseBatchEntityResolutionSnapshots = useBatchEntityResolutionSnapshots;
        UseCoalescedPersistenceTransactions = useCoalescedPersistenceTransactions;
    }

    /// <summary>Embedding dimensionality. Small by design — vector width is not what is being measured.</summary>
    public int Dimensions { get; }

    /// <summary>Fixed product-driver pool size fingerprinted by concurrency artifacts.</summary>
    public int MaxConnectionPoolSize { get; }

    /// <summary>Whether batch-scoped owner/type entity candidate snapshots are enabled.</summary>
    public bool UseBatchEntityResolutionSnapshots { get; }

    /// <summary>Whether successful logical persistence operations share one atomic transaction.</summary>
    public bool UseCoalescedPersistenceTransactions { get; }

    /// <summary>Scoped service provider for resolving memory services.</summary>
    public IServiceProvider Services => _scope.ServiceProvider;

    /// <summary>Dataset tier mounted into this profile.</summary>
    public PerfScale Scale { get; }

    /// <summary>Verified snapshot/restore metadata; null for the directly seeded scale-S tier.</summary>
    public ScaleDatasetInfo? ScaleDataset { get; private set; }

    /// <summary>Raw driver, for bulk fixture seeding that would be pointlessly slow through the services.</summary>
    public IDriver Driver { get; private set; } = null!;

    /// <summary>Container identifier exposed only to explicit resource-capacity laboratory scenarios.</summary>
    internal string ContainerId => _container?.Id ?? throw new InvalidOperationException("Neo4j is not running.");

    /// <summary>Scenario-scoped dependency latency; unset outside an explicitly degraded scenario.</summary>
    public PerfDependencyLatency DependencyLatency { get; } = new();

    public static async Task<HermeticProfile> StartAsync(
        int dimensions,
        TimeSpan embeddingLatency,
        TimeSpan modelLatency,
        TextWriter log,
        IReadOnlyList<ScriptedChatClient.Rule>? scriptedRules = null,
        CancellationToken cancellationToken = default)
        => await StartAsync(
            dimensions,
            embeddingLatency,
            modelLatency,
            log,
            PerfScale.Small,
            scriptedRules,
            cancellationToken).ConfigureAwait(false);

    public static async Task<HermeticProfile> StartAsync(
        int dimensions,
        TimeSpan embeddingLatency,
        TimeSpan modelLatency,
        TextWriter log,
        PerfScale scale,
        IReadOnlyList<ScriptedChatClient.Rule>? scriptedRules = null,
        CancellationToken cancellationToken = default,
        int maxConnectionPoolSize = 100,
        bool useUnifiedExtraction = false,
        bool useBatchEntityResolutionSnapshots = true,
        bool useCoalescedPersistenceTransactions = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConnectionPoolSize);
        var scaleRunVolume = scale == PerfScale.Medium
            ? await ScaleMDataset.PrepareRunVolumeAsync(dimensions, log, cancellationToken).ConfigureAwait(false)
            : null;
        var profile = new HermeticProfile(
            dimensions, scale, scaleRunVolume, maxConnectionPoolSize,
            useBatchEntityResolutionSnapshots, useCoalescedPersistenceTransactions);
        try
        {
            await profile.InitializeAsync(
                    embeddingLatency, modelLatency, log, scriptedRules, useUnifiedExtraction,
                    useBatchEntityResolutionSnapshots, useCoalescedPersistenceTransactions,
                    cancellationToken)
                .ConfigureAwait(false);
            return profile;
        }
        catch
        {
            await profile.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task InitializeAsync(
        TimeSpan embeddingLatency, TimeSpan modelLatency, TextWriter log,
        IReadOnlyList<ScriptedChatClient.Rule>? scriptedRules, bool useUnifiedExtraction,
        bool useBatchEntityResolutionSnapshots,
        bool useCoalescedPersistenceTransactions,
        CancellationToken cancellationToken)
    {
        log.WriteLine($"perf: starting {Image} (Testcontainers)…");
        var builder = new Neo4jBuilder(Image)
            .WithEnvironment("NEO4J_AUTH", $"{ContainerUser}/{ContainerPassword}");
        if (_scaleRunVolume is not null)
            builder = builder.WithVolumeMount(_scaleRunVolume.Volume, "/data");

        _container = builder.Build();
        await _container.StartAsync(cancellationToken).ConfigureAwait(false);

        var uri = _container.GetConnectionString();
        Driver = GraphDatabase.Driver(uri, AuthTokens.Basic(ContainerUser, ContainerPassword));

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

        services.AddNeo4jAgentMemory(
            memory =>
            {
                memory.Extraction.UseBatchEmbeddingRequests = true;
                memory.Extraction.UseBatchEntityResolutionSnapshots = useBatchEntityResolutionSnapshots;
                memory.Extraction.UseCoalescedPersistenceTransactions =
                    useCoalescedPersistenceTransactions;
            },
            neo4j =>
            {
                neo4j.Uri = uri;
                neo4j.Username = ContainerUser;
                neo4j.Password = ContainerPassword;
                neo4j.Database = "neo4j";
                neo4j.EmbeddingDimensions = Dimensions;
                neo4j.MaxConnectionPoolSize = MaxConnectionPoolSize;
            },
            // A non-null delegate is what opts the LLM extractors in; without it the Core no-op stubs
            // stay registered and a post-turn scenario would measure extraction that never happens.
            llm =>
            {
                llm.UseUnifiedExtraction = useUnifiedExtraction;
                llm.UseMultiSessionBatchExtraction = useUnifiedExtraction;
            });

        // LAB-P0 intercepts only its explicit source marker and delegates every other extraction.
        // Register before the provider is built so the real pipeline still owns resolution/persistence.
        FrozenExtractionOverrides.Decorate(services);

        // Registered exactly as a host source would be, but the shared MemoryOptions keep GraphRAG
        // disabled. PERF-R-08 builds an isolated production assembler with EnableGraphRag=true; every
        // other scenario continues to exercise shipped defaults and cannot call this source.
        services.AddSingleton<IGraphRagContextSource, DeterministicGraphRagContextSource>();

        DecorateTransactionRunner(services, DependencyLatency);

        // Counting wrappers sit outermost so they observe every call the product makes, including the
        // ones issued from inside the extraction pipeline.
        services.RemoveAll<IEmbeddingGenerator<string, Embedding<float>>>();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new CountingEmbeddingGenerator(
                new LatencyInjectingEmbeddingGenerator(
                    new DeterministicEmbeddingGenerator(Dimensions),
                    embeddingLatency,
                    DependencyLatency)));

        services.RemoveAll<IChatClient>();
        services.AddSingleton<IChatClient>(
            new CountingChatClient(new ScriptedChatClient(modelLatency, payload: null, rules: scriptedRules)));

        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateAsyncScope();
        _scopeCreated = true;

        log.WriteLine("perf: bootstrapping schema…");
        await Services.GetRequiredService<ISchemaBootstrapper>()
            .BootstrapAsync(cancellationToken).ConfigureAwait(false);

        await using var session = Driver.AsyncSession();
        await session.RunAsync("CALL db.awaitIndexes(120)").ConfigureAwait(false);
        if (_scaleRunVolume is not null)
        {
            var verified = await ScaleMDataset
                .VerifyRestoredAsync(Driver, cancellationToken)
                .ConfigureAwait(false);
            ScaleDataset = _scaleRunVolume.Info with { MemoryNodeCount = verified };
            log.WriteLine($"perf: verified {verified:N0} scale-M memory nodes after restore.");
        }
        log.WriteLine("perf: schema ready.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_scopeCreated) await _scope.DisposeAsync().ConfigureAwait(false);
        if (_provider is not null) await _provider.DisposeAsync().ConfigureAwait(false);
        if (Driver is not null) await Driver.DisposeAsync().ConfigureAwait(false);
        if (_container is not null) await _container.DisposeAsync().ConfigureAwait(false);
        if (_scaleRunVolume is not null)
            await _scaleRunVolume.Volume.DisposeAsync().ConfigureAwait(false);
    }

    private static void DecorateTransactionRunner(
        IServiceCollection services,
        PerfDependencyLatency dependencyLatency)
    {
        var descriptor = services.LastOrDefault(
            item => item.ServiceType == typeof(INeo4jTransactionRunner))
            ?? throw new InvalidOperationException("Neo4j transaction runner was not registered.");

        services.Remove(descriptor);
        services.Add(new ServiceDescriptor(
            typeof(INeo4jTransactionRunner),
            provider => new LatencyInjectingTransactionRunner(
                CreateTransactionRunner(provider, descriptor),
                dependencyLatency),
            descriptor.Lifetime));
    }

    private static INeo4jTransactionRunner CreateTransactionRunner(
        IServiceProvider provider,
        ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is INeo4jTransactionRunner instance)
            return instance;

        if (descriptor.ImplementationFactory is not null)
            return (INeo4jTransactionRunner)descriptor.ImplementationFactory(provider);

        if (descriptor.ImplementationType is not null)
            return (INeo4jTransactionRunner)ActivatorUtilities.CreateInstance(
                provider, descriptor.ImplementationType);

        throw new InvalidOperationException("Neo4j transaction runner registration has no implementation.");
    }

    /// <summary>
    /// Adds a fixed delay to every embedding request so the hermetic profile can reproduce the latency
    /// shape of a remote provider without a network. Separate from the counting decorator so latency and
    /// accounting stay independently composable.
    /// </summary>
    private sealed class LatencyInjectingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly IEmbeddingGenerator<string, Embedding<float>> _inner;
        private readonly TimeSpan _runWideDelay;
        private readonly PerfDependencyLatency _dependencyLatency;

        public LatencyInjectingEmbeddingGenerator(
            IEmbeddingGenerator<string, Embedding<float>> inner,
            TimeSpan runWideDelay,
            PerfDependencyLatency dependencyLatency)
        {
            _inner = inner;
            _runWideDelay = runWideDelay;
            _dependencyLatency = dependencyLatency;
        }

        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var preset = _dependencyLatency.Current;
            var delay = _dependencyLatency.ResolveEmbeddingDelay(_runWideDelay);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                if (preset is not null)
                {
                    PerfCollector.Current?.Add("injected.embedding_delay.calls");
                    PerfCollector.Current?.Add(
                        "injected.embedding_delay.ms",
                        checked((long)delay.TotalMilliseconds));
                }
            }
            return await _inner.GenerateAsync(values, options, cancellationToken).ConfigureAwait(false);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : _inner.GetService(serviceType, serviceKey);

        public void Dispose() => _inner.Dispose();
    }

    /// <summary>
    /// Inserts deterministic waiting inside the real transaction span. The original work delegate still
    /// receives the product's instrumented query runner, so query counting and behavior are unchanged.
    /// </summary>
    private sealed class LatencyInjectingTransactionRunner : INeo4jTransactionRunner, INeo4jAtomicTransactionRunner
    {
        private readonly INeo4jTransactionRunner _inner;
        private readonly PerfDependencyLatency _dependencyLatency;
        private readonly INeo4jAtomicTransactionRunner _atomicInner;

        public LatencyInjectingTransactionRunner(
            INeo4jTransactionRunner inner,
            PerfDependencyLatency dependencyLatency)
        {
            _inner = inner;
            _dependencyLatency = dependencyLatency;
            _atomicInner = inner as INeo4jAtomicTransactionRunner
                ?? throw new InvalidOperationException("The decorated Neo4j runner must support atomic write units.");
        }

        public Task<T> ReadAsync<T>(
            Func<IAsyncQueryRunner, Task<T>> work,
            CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(
                async runner =>
                {
                    await DelayAsync(cancellationToken).ConfigureAwait(false);
                    return await work(runner).ConfigureAwait(false);
                },
                cancellationToken);

        public Task ReadAsync(
            Func<IAsyncQueryRunner, Task> work,
            CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(
                async runner =>
                {
                    await DelayAsync(cancellationToken).ConfigureAwait(false);
                    await work(runner).ConfigureAwait(false);
                },
                cancellationToken);

        public Task<T> WriteAsync<T>(
            Func<IAsyncQueryRunner, Task<T>> work,
            CancellationToken cancellationToken = default) =>
            _inner.WriteAsync(
                async runner =>
                {
                    await DelayAsync(cancellationToken).ConfigureAwait(false);
                    return await work(runner).ConfigureAwait(false);
                },
                cancellationToken);

        public Task WriteAsync(
            Func<IAsyncQueryRunner, Task> work,
            CancellationToken cancellationToken = default) =>
            _inner.WriteAsync(
                async runner =>
                {
                    await DelayAsync(cancellationToken).ConfigureAwait(false);
                    await work(runner).ConfigureAwait(false);
                },
                cancellationToken);

        public Task<T> ExecuteAtomicWriteAsync<T>(
            Func<CancellationToken, Task<T>> work,
            CancellationToken cancellationToken = default) =>
            _atomicInner.ExecuteAtomicWriteAsync(work, cancellationToken);

        private async Task DelayAsync(CancellationToken cancellationToken)
        {
            var delay = _dependencyLatency.Current?.DatabaseDelay ?? TimeSpan.Zero;
            if (delay <= TimeSpan.Zero) return;

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            PerfCollector.Current?.Add("injected.database_delay.calls");
            PerfCollector.Current?.Add(
                "injected.database_delay.ms",
                checked((long)delay.TotalMilliseconds));
        }
    }
}
