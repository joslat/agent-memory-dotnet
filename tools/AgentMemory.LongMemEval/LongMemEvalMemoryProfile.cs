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
        bool multiSessionBatch = true,
        int maxConcurrentBatchesPerExtraction = 1,
        int maxConcurrentExtractionBatches = 0,
        bool usePredicateVocabulary = false,
        AssistantContentMode assistantContent = AssistantContentMode.Ignore,
        bool resolveTemporalQueries = false,
        bool rescueShortOwnerResults = false,
        string? graphRagIndexName = null,
        int? extractionSeed = null)
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
                    multiSessionBatch,
                    maxConcurrentBatchesPerExtraction,
                    maxConcurrentExtractionBatches,
                    usePredicateVocabulary,
                    assistantContent,
                    resolveTemporalQueries,
                    rescueShortOwnerResults,
                    graphRagIndexName,
                    extractionSeed,
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
        bool multiSessionBatch,
        int maxConcurrentBatchesPerExtraction,
        int maxConcurrentExtractionBatches,
        bool usePredicateVocabulary,
        AssistantContentMode assistantContent,
        bool resolveTemporalQueries,
        bool rescueShortOwnerResults,
        string? graphRagIndexName,
        int? extractionSeed,
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
            assistantContent,
            resolveTemporalQueries,
            rescueShortOwnerResults,
            graphRagIndexName,
            multiSessionBatch,
            extractionSeed);

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
        AssistantContentMode assistantContent,
        bool resolveTemporalQueries,
        bool rescueShortOwnerResults,
        string? graphRagIndexName,
        bool multiSessionBatch = true,
        int? extractionSeed = null)
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
                // These select DIFFERENT extractors and were previously driven by one boolean, so the
                // single-session unified path could not be measured at all: every one of the 55 recorded
                // runs was unified+multi-session, i.e. LlmMultiSessionUnifiedMemoryExtractor via
                // ExtractBatchAsync. An ordinary consumer who enables UseUnifiedExtraction gets
                // LlmUnifiedMemoryExtractor instead, which no measurement had ever exercised.
                // multiSessionBatch defaults to true, so every existing base and manifest is unaffected.
                options.UseUnifiedExtraction = enableBatchedPreparation;
                options.UseMultiSessionBatchExtraction = enableBatchedPreparation && multiSessionBatch;
                options.MaxConcurrentBatchesPerExtraction = maxConcurrentBatchesPerExtraction;
                options.MaxConcurrentExtractionBatches = maxConcurrentExtractionBatches;
                options.UsePredicateVocabulary = usePredicateVocabulary;
                options.AssistantContent = assistantContent;
                // 30.1. The one lever the provider offers against extraction nondeterminism, and it
                // had no writer here: Temperature is already 0 and this deployment REJECTS an explicit
                // zero, so the request runs at the provider default of 1.0. Three cold builds of one
                // configuration agreed on 7.5% of their canonical triples and scored 25 accuracy points
                // apart. Null (the default) sends nothing and reproduces every sealed measurement; a
                // value is best-effort, which is why whether it helps is measured rather than assumed.
                options.Seed = extractionSeed;
            }
            : null;
        services.AddNeo4jAgentMemory(
            // K9.1: the instance overload. The Action<MemoryOptions> one cannot set anything -
            // MemoryOptions is an init-only record, so a configure lambda can neither assign its
            // properties nor keep a `with` expression's result.
            new MemoryOptions
            {
                EnableGraphRag = graphRagIndexName is not null,
                // 13.3. Off by default so every sealed measurement keeps taking the path it was taken
                // under; the ablation turns it on explicitly and re-runs the SAME frozen corpus.
                ResolveTemporalQueries = resolveTemporalQueries,
                // 22.4. A coverage lever with ZERO harness references until now: the one option aimed
                // squarely at "a short scoped result falls back to a bounded scan" could not be set
                // from the benchmark, so the mechanism most directly matching the measured failure
                // mode was the one thing no run could exercise.
                RescueShortOwnerResults = rescueShortOwnerResults,
            },
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
