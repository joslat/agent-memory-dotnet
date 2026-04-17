using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.GraphRagAdapter.Retrieval;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.GraphRagAdapter.Internal;
using Neo4j.Driver;

namespace Neo4j.AgentMemory.GraphRagAdapter;

/// <summary>
/// Implements <see cref="IGraphRagContextSource"/> by delegating to a Neo4j-backed
/// <see cref="IRetriever"/>. Supports vector, fulltext, hybrid, and graph-enriched modes.
/// </summary>
public sealed class Neo4jGraphRagContextSource : IGraphRagContextSource
{
    private readonly IRetriever _retriever;
    private readonly GraphRagAdapterOptions _options;
    private readonly ILogger<Neo4jGraphRagContextSource> _logger;

    /// <summary>
    /// Production constructor resolved via DI.
    /// </summary>
    public Neo4jGraphRagContextSource(
        IDriver driver,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        IOptions<GraphRagAdapterOptions> options,
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
        GraphRagAdapterOptions options,
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GraphRAG retrieval failed for session {SessionId}", request.SessionId);
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

    private static IRetriever CreateRetriever(
        IDriver driver,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        GraphRagAdapterOptions options)
    {
        return options.SearchMode switch
        {
            GraphRagSearchMode.Vector => new AdapterVectorRetriever(
                driver,
                options.IndexName,
                embeddingGenerator,
                options.RetrievalQuery),

            GraphRagSearchMode.Fulltext => new AdapterFulltextRetriever(
                driver,
                options.FulltextIndexName ?? options.IndexName,
                options.RetrievalQuery,
                options.FilterStopWords),

            GraphRagSearchMode.Hybrid => new AdapterHybridRetriever(
                driver,
                options.IndexName,
                options.FulltextIndexName ?? options.IndexName,
                embeddingGenerator,
                options.RetrievalQuery,
                options.FilterStopWords),

            // Graph mode uses vector search with a custom graph traversal query
            GraphRagSearchMode.Graph => new AdapterVectorRetriever(
                driver,
                options.IndexName,
                embeddingGenerator,
                options.RetrievalQuery
                    ?? "WITH node, score MATCH (node)-[:RELATED_TO*1..2]-(related) RETURN node.text + ' -> ' + related.text AS text, score"),

            _ => throw new ArgumentOutOfRangeException(nameof(options.SearchMode),
                     $"Unsupported search mode: {options.SearchMode}")
        };
    }
}
