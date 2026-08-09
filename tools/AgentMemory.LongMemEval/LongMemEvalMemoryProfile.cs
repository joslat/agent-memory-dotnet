using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Extraction.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
        bool usePredicateVocabulary = false,
        string? graphRagIndexName = null)
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
                    graphRagIndexName,
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
        string? graphRagIndexName,
        CancellationToken cancellationToken)
    {
        log.WriteLine($"longmemeval: starting {Image}...");
        var builder = new Neo4jBuilder(Image)
            .WithEnvironment("NEO4J_AUTH", $"{User}/{Password}");
        if (!string.IsNullOrWhiteSpace(volumeName))
            builder = builder.WithVolumeMount(volumeName, "/data");

        _container = builder.Build();
        await _container.StartAsync(cancellationToken).ConfigureAwait(false);

        var services = ConfigureServices(
            _container.GetConnectionString(),
            embeddingGenerator,
            extractionChatClient,
            memoryMode,
            extractionModelId,
            embeddingDimensions,
            enableBatchedPreparation,
            maxConcurrentBatchesPerExtraction,
            maxConcurrentExtractionBatches,
            usePredicateVocabulary,
            graphRagIndexName);

        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateAsyncScope();
        _scopeCreated = true;

        await Services.GetRequiredService<ISchemaBootstrapper>()
            .BootstrapAsync(cancellationToken)
            .ConfigureAwait(false);
        log.WriteLine("longmemeval: schema ready.");
    }

    /// <summary>
    /// The profile's DI wiring, separated from container startup so it can be asserted on without a
    /// live Neo4j.
    /// </summary>
    /// <remarks>
    /// K6 measured GraphRAG returning zero items and very nearly reported that as a property of the
    /// surface. It was a wiring fault, and it cost a full evaluation run to find. Registration that
    /// can only be exercised by paying for a run is registration that gets verified by spending
    /// money, so this is reachable from a test instead.
    /// </remarks>
    internal static ServiceCollection ConfigureServices(
        string neo4jUri,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        IChatClient? extractionChatClient,
        LongMemEvalMemoryMode memoryMode,
        string? extractionModelId,
        int embeddingDimensions,
        bool enableBatchedPreparation,
        int maxConcurrentBatchesPerExtraction,
        int maxConcurrentExtractionBatches,
        bool usePredicateVocabulary,
        string? graphRagIndexName)
    {
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
            // K9.1: the instance overload. The Action<MemoryOptions> one cannot set anything -
            // MemoryOptions is an init-only record, so a configure lambda can neither assign its
            // properties nor keep a `with` expression's result.
            new MemoryOptions { EnableGraphRag = graphRagIndexName is not null },
            neo4j =>
            {
                neo4j.Uri = neo4jUri;
                neo4j.Username = User;
                neo4j.Password = Password;
                neo4j.Database = "neo4j";
                neo4j.EmbeddingDimensions = embeddingDimensions;
            }, configureLlm);

        if (graphRagIndexName is not null)
        {
            // Deliberately pointed at one of the memory layer's own vector indexes, because this
            // corpus contains no separate knowledge graph - which is the setting upstream actually
            // targets. That limits what the result can mean, and the limit is recorded rather than
            // discovered afterwards.
            //
            // The retrieval query is not optional decoration. K10: with the default projection, a
            // Fact node has no `text` or `content` property, so every item's prompt text becomes the
            // driver's dump of the whole node - embedding vector included. Projecting the triple
            // explicitly also carries fact_id through into metadata, which is what makes the
            // duplication measurement possible at all.
            services.AddGraphRagAdapter(graphRag =>
            {
                graphRag.IndexName = graphRagIndexName;
                graphRag.SearchMode = GraphRagSearchMode.Vector;
                graphRag.RetrievalQuery =
                    "RETURN node.subject + ' ' + node.predicate + ' ' + node.object AS text, " +
                    "node.id AS fact_id, score";
            });
        }

        services.RemoveAll<IEmbeddingGenerator<string, Embedding<float>>>();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            embeddingGenerator);

        if (extractionChatClient is not null)
            services.AddSingleton(extractionChatClient);

        return services;
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
