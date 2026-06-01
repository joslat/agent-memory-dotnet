using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Neo4j.Retrieval;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Retrieval.Internal;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Services;

/// <summary>
/// Implements <see cref="IGraphRagContextSource"/> by delegating to a Neo4j-backed
/// <see cref="IRetriever"/>. Supports vector, fulltext, hybrid, and graph-enriched modes.
/// </summary>
public sealed class Neo4jGraphRagContextSource : IGraphRagContextSource
{
    private readonly IRetriever _retriever;
    private readonly GraphRagOptions _options;
    private readonly ILogger<Neo4jGraphRagContextSource> _logger;

    /// <summary>
    /// Production constructor resolved via DI.
    /// </summary>
    public Neo4jGraphRagContextSource(
        IDriver driver,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        IOptions<GraphRagOptions> options,
        ILogger<Neo4jGraphRagContextSource> logger)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(embeddingGenerator);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _logger = logger;
        _retriever = CreateRetriever(driver, embeddingGenerator, _options);
    }

    /// <summary>
    /// Testing constructor: accepts a pre-built retriever.
    /// </summary>
    internal Neo4jGraphRagContextSource(
        IRetriever retriever,
        GraphRagOptions options,
        ILogger<Neo4jGraphRagContextSource> logger)
    {
        _retriever = retriever;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<GraphRagContextResult> GetContextAsync(
        GraphRagContextRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var topK = request.TopK > 0 ? request.TopK : _options.TopK;
            var result = await _retriever.SearchAsync(request.Query, topK, cancellationToken)
                .ConfigureAwait(false);

            var items = result.Items.Select(MapItem).ToList();
            return new GraphRagContextResult { Items = items };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Honor cancellation — do not mask it as an empty result.
            throw;
        }
        catch (Exception ex)
        {
            // Resilient path: GraphRAG is best-effort context. Log at Warning with full context
            // so the failure is observable (and not indistinguishable from a genuine empty result).
            _logger.LogWarning(ex, "GraphRAG retrieval failed for session {SessionId}; returning empty context", request.SessionId);
            return new GraphRagContextResult { Items = Array.Empty<GraphRagContextItem>() };
        }
    }

    private static GraphRagContextItem MapItem(RetrieverResultItem item)
    {
        double score = 0;
        var metadata = new Dictionary<string, object>();

        if (item.Metadata is not null)
        {
            foreach (var (key, value) in item.Metadata)
            {
                if (key == "score" && value is double d)
                    score = d;
                else if (value is not null)
                    metadata[key] = value;
            }
        }

        return new GraphRagContextItem
        {
            Text = item.Content,
            Score = score,
            Metadata = metadata
        };
    }

    /// <summary>
    /// Pure selection: maps the configured <see cref="GraphRagSearchMode"/> to its retriever.
    /// All mode-specific query/traversal behavior lives inside the retrievers themselves.
    /// </summary>
    internal static IRetriever CreateRetriever(
        IDriver driver,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        GraphRagOptions options)
    {
        return options.SearchMode switch
        {
            GraphRagSearchMode.Vector => new VectorRetriever(
                driver,
                options.IndexName,
                embeddingGenerator,
                options.RetrievalQuery),

            GraphRagSearchMode.Fulltext => new FulltextRetriever(
                driver,
                options.FulltextIndexName ?? options.IndexName,
                options.RetrievalQuery,
                options.FilterStopWords),

            GraphRagSearchMode.Hybrid => new HybridRetriever(
                driver,
                options.IndexName,
                options.FulltextIndexName ?? options.IndexName,
                embeddingGenerator,
                options.RetrievalQuery,
                options.FilterStopWords),

            GraphRagSearchMode.Graph => new GraphRetriever(
                driver,
                options.IndexName,
                embeddingGenerator,
                options.MaxTraversalHops),

            _ => throw new ArgumentOutOfRangeException(nameof(options.SearchMode),
                     $"Unsupported search mode: {options.SearchMode}")
        };
    }
}
