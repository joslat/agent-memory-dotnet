using Microsoft.Extensions.AI;
using Neo4j.AgentMemory.GraphRagAdapter.Retrieval;
using Neo4j.Driver;

namespace Neo4j.AgentMemory.GraphRagAdapter.Internal;

/// <summary>
/// Vector similarity retriever backed by a Neo4j vector index.
/// </summary>
internal sealed class AdapterVectorRetriever : IRetriever
{
    private readonly IDriver _driver;
    private readonly string _indexName;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly string? _retrievalQuery;

    internal AdapterVectorRetriever(
        IDriver driver,
        string indexName,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        string? retrievalQuery = null)
    {
        _driver = driver;
        _indexName = indexName;
        _embeddingGenerator = embeddingGenerator;
        _retrievalQuery = retrievalQuery;
    }

    public async Task<RetrieverResult> SearchAsync(
        string queryText, int topK, CancellationToken cancellationToken = default)
    {
        var embedding = await _embeddingGenerator
            .GenerateVectorAsync(queryText, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        string cypher = _retrievalQuery is not null
            ? $"""
               CALL db.index.vector.queryNodes($index, $k, $embedding)
               YIELD node, score
               WITH node, score
               ORDER BY score DESC
               LIMIT $k
               {_retrievalQuery}
               LIMIT $k
               """
            : """
              CALL db.index.vector.queryNodes($index, $k, $embedding)
              YIELD node, score
              WITH node, score
              ORDER BY score DESC
              LIMIT $k
              RETURN node, score
              """;

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

        var items = records.Select(r =>
            _retrievalQuery is not null
                ? FormatCypherResult(r)
                : FormatStandardResult(r)
        ).ToList();

        return new RetrieverResult(items);
    }

    private static RetrieverResultItem FormatStandardResult(IRecord record)
    {
        var node = record["node"].As<INode>();
        var score = record["score"].As<double>();
        var content = node.Properties.TryGetValue("text", out var text)
            ? text?.ToString() ?? ""
            : node.Properties.TryGetValue("content", out var c)
                ? c?.ToString() ?? ""
                : node.ToString()!;
        return new RetrieverResultItem(content, new Dictionary<string, object?> { ["score"] = score });
    }

    internal static RetrieverResultItem FormatCypherResult(IRecord record)
    {
        var data = record.Keys.ToDictionary<string, string, object?>(k => k, k => record[k]);
        string content;
        if (data.Remove("text", out var textVal) && textVal is not null)
            content = textVal.ToString()!;
        else
            content = (data.Values.FirstOrDefault(v => v is string)
                       ?? data.Values.FirstOrDefault(v => v is not null))?.ToString() ?? "";
        return new RetrieverResultItem(content, data.Count > 0 ? data! : null);
    }
}
