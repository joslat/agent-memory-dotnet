using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Extraction.Llm;
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
        IChatClient? extractionChatClient,
        LongMemEvalMemoryMode memoryMode,
        string? extractionModelId,
        int embeddingDimensions,
        TextWriter log,
        CancellationToken cancellationToken,
        string? volumeName = null,
        bool enableBatchedPreparation = false,
        int maxConcurrentBatchesPerExtraction = 1,
        int maxConcurrentExtractionBatches = 0,
        bool usePredicateVocabulary = false)
    {
        ArgumentNullException.ThrowIfNull(embeddingGenerator);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(embeddingDimensions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            maxConcurrentBatchesPerExtraction);
        ArgumentOutOfRangeException.ThrowIfNegative(maxConcurrentExtractionBatches);

        if (memoryMode.UsesExtraction() && extractionChatClient is null)
        {
            throw new ArgumentNullException(
                nameof(extractionChatClient), "Structured and hybrid modes require a real extraction chat client.");
        }
        var profile = new LongMemEvalMemoryProfile();
        try
        {
            await profile.InitializeAsync(
                    embeddingGenerator,
                    extractionChatClient,
                    memoryMode,
                    extractionModelId,
                    embeddingDimensions,
                    log,
                    volumeName,
                    enableBatchedPreparation,
                    maxConcurrentBatchesPerExtraction,
                    maxConcurrentExtractionBatches,
                    usePredicateVocabulary,
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
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        IChatClient? extractionChatClient,
        LongMemEvalMemoryMode memoryMode,
        string? extractionModelId,
        int embeddingDimensions,
        TextWriter log,
        string? volumeName,
        bool enableBatchedPreparation,
        int maxConcurrentBatchesPerExtraction,
        int maxConcurrentExtractionBatches,
        bool usePredicateVocabulary,
        CancellationToken cancellationToken)
    {
        log.WriteLine($"longmemeval: starting {Image}...");
        var builder = new Neo4jBuilder(Image)
            .WithEnvironment("NEO4J_AUTH", $"{User}/{Password}");
        if (!string.IsNullOrWhiteSpace(volumeName))
            builder = builder.WithVolumeMount(volumeName, "/data");

        _container = builder.Build();
        await _container.StartAsync(cancellationToken).ConfigureAwait(false);

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        Action<LlmExtractionOptions>? configureLlm = memoryMode.UsesExtraction()
            ? options =>
            {
                options.ModelId = extractionModelId;
                options.Temperature = 0;
                options.MaxRetries = 2;
                options.UseJsonResponseFormat = true;
                options.UseUnifiedExtraction = enableBatchedPreparation;
                options.UseMultiSessionBatchExtraction = enableBatchedPreparation;
                options.MaxConcurrentBatchesPerExtraction = maxConcurrentBatchesPerExtraction;
                options.MaxConcurrentExtractionBatches = maxConcurrentExtractionBatches;
                options.UsePredicateVocabulary = usePredicateVocabulary;
            }
            : null;
        services.AddNeo4jAgentMemory(
            memory => { },
            neo4j =>
            {
                neo4j.Uri = _container.GetConnectionString();
                neo4j.Username = User;
                neo4j.Password = Password;
                neo4j.Database = "neo4j";
                neo4j.EmbeddingDimensions = embeddingDimensions;
            }, configureLlm);

        services.RemoveAll<IEmbeddingGenerator<string, Embedding<float>>>();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            embeddingGenerator);

        if (extractionChatClient is not null)
            services.AddSingleton(extractionChatClient);
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
