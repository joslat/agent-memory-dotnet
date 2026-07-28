using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Testcontainers.Neo4j;

namespace AgentMemory.LongMemEval;

/// <summary>A disposable, pinned Neo4j profile for public LongMemEval characterization runs.</summary>
internal sealed class LongMemEvalMemoryProfile : IAsyncDisposable
{
    private const string Image = "neo4j:5.26";
    private const string User = "neo4j";
    private const string Password = "longmemeval-password";

    private Neo4jContainer? _container;
    private ServiceProvider? _provider;
    private AsyncServiceScope _scope;
    private bool _scopeCreated;

    public IServiceProvider Services => _scope.ServiceProvider;

    public static async Task<LongMemEvalMemoryProfile> StartAsync(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        int embeddingDimensions,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(embeddingGenerator);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(embeddingDimensions);

        var profile = new LongMemEvalMemoryProfile();
        try
        {
            await profile.InitializeAsync(
                embeddingGenerator, embeddingDimensions, log, cancellationToken).ConfigureAwait(false);
            return profile;
        }
        catch
        {
            await profile.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task InitializeAsync(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        int embeddingDimensions,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        log.WriteLine($"longmemeval: starting {Image}...");
        _container = new Neo4jBuilder(Image)
            .WithEnvironment("NEO4J_AUTH", $"{User}/{Password}")
            .Build();
        await _container.StartAsync(cancellationToken).ConfigureAwait(false);

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddNeo4jAgentMemory(
            memory => { },
            neo4j =>
            {
                neo4j.Uri = _container.GetConnectionString();
                neo4j.Username = User;
                neo4j.Password = Password;
                neo4j.Database = "neo4j";
                neo4j.EmbeddingDimensions = embeddingDimensions;
            });

        services.RemoveAll<IEmbeddingGenerator<string, Embedding<float>>>();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            embeddingGenerator);

        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateAsyncScope();
        _scopeCreated = true;

        await Services.GetRequiredService<ISchemaBootstrapper>()
            .BootstrapAsync(cancellationToken)
            .ConfigureAwait(false);
        log.WriteLine("longmemeval: schema ready.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_scopeCreated)
            await _scope.DisposeAsync().ConfigureAwait(false);
        if (_provider is not null)
            await _provider.DisposeAsync().ConfigureAwait(false);
        if (_container is not null)
            await _container.DisposeAsync().ConfigureAwait(false);
    }
}
