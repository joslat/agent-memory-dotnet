using Microsoft.Extensions.AI;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Retrieval.Internal;

/// <summary>
/// Graph-aware retriever: seeds a set of nodes by vector similarity, then performs a bounded
/// multi-hop traversal over <c>RELATED_TO</c> relationships to surface contextually-connected
/// neighbours. This is the real implementation behind <see cref="AgentMemory.Abstractions.Domain.GraphRagSearchMode.Graph"/>
/// (previously emulated by a <see cref="VectorRetriever"/> with a hand-written Cypher tail).
/// </summary>
internal sealed class GraphRetriever : IRetriever
{
    private const int MinHops = 1;
    private const int MaxHops = 5;

    private readonly IDriver _driver;
    private readonly string _indexName;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly int _maxTraversalHops;

    internal GraphRetriever(
        IDriver driver,
        string indexName,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        int maxTraversalHops = 2)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        ArgumentNullException.ThrowIfNull(embeddingGenerator);
        if (maxTraversalHops is < MinHops or > MaxHops)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxTraversalHops), maxTraversalHops,
                $"Graph traversal hops must be between {MinHops} and {MaxHops}.");
        }

        _driver = driver;
        _indexName = indexName;
        _embeddingGenerator = embeddingGenerator;
        _maxTraversalHops = maxTraversalHops;
    }

    public async Task<RetrieverResult> SearchAsync(
        string queryText, int topK, CancellationToken cancellationToken = default)
    {
        var embedding = await _embeddingGenerator
            .GenerateVectorAsync(queryText, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        string cypher = BuildTraversalCypher(_maxTraversalHops);

        var parameters = new Dictionary<string, object?>
        {
            ["index"] = _indexName,
            ["k"] = topK,
            ["embedding"] = embedding.ToArray()
        };

        var (records, _, _) = await _driver.ExecutableQuery(cypher)
            .WithParameters(parameters)
            .WithConfig(new QueryConfig(routing: RoutingControl.Readers))
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = records.Select(FormatGraphResult).ToList();
        return new RetrieverResult(items);
    }

    /// <summary>
    /// Builds the vector-seed + multi-hop traversal Cypher. The hop bound is interpolated as an
    /// integer literal (Cypher forbids parameterizing variable-length path bounds); it is validated
    /// to a small constant range in the constructor and is never derived from caller-supplied text,
    /// so this interpolation cannot be an injection vector.
    /// </summary>
    internal static string BuildTraversalCypher(int maxTraversalHops) => $$"""
        CALL db.index.vector.queryNodes($index, $k, $embedding)
        YIELD node AS seed, score
        WITH seed, score
        ORDER BY score DESC
        LIMIT $k
        MATCH path = (seed)-[:RELATED_TO*1..{{maxTraversalHops}}]-(related)
        WITH seed, related, score, length(path) AS hops
        RETURN
          coalesce(seed.text, seed.content, seed.name, '') AS seedText,
          coalesce(related.text, related.content, related.name, '') AS relatedText,
          score,
          hops
        ORDER BY score DESC, hops ASC
        LIMIT $k
        """;

    internal static RetrieverResultItem FormatGraphResult(IRecord record)
    {
        var seedText = record["seedText"].As<string>() ?? string.Empty;
        var relatedText = record["relatedText"].As<string>() ?? string.Empty;
        var score = record["score"].As<double>();
        var hops = record["hops"].As<int>();

        string content = string.IsNullOrEmpty(relatedText)
            ? seedText
            : $"{seedText} → {relatedText}";

        return new RetrieverResultItem(content, new Dictionary<string, object?>
        {
            ["score"] = score,
            ["hops"] = hops
        });
    }
}
