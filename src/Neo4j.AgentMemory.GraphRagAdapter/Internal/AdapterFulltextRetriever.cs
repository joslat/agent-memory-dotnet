using Neo4j.AgentMemory.GraphRagAdapter.Retrieval;
using Neo4j.Driver;

namespace Neo4j.AgentMemory.GraphRagAdapter.Internal;

/// <summary>
/// BM25 fulltext retriever backed by a Neo4j fulltext index.
/// </summary>
internal sealed class AdapterFulltextRetriever : IRetriever
{
    private readonly IDriver _driver;
    private readonly string _indexName;
    private readonly string? _retrievalQuery;
    private readonly bool _filterStopWords;

    internal AdapterFulltextRetriever(
        IDriver driver,
        string indexName,
        string? retrievalQuery = null,
        bool filterStopWords = true)
    {
        _driver = driver;
        _indexName = indexName;
        _retrievalQuery = retrievalQuery;
        _filterStopWords = filterStopWords;
    }

    public async Task<RetrieverResult> SearchAsync(
        string queryText, int topK, CancellationToken cancellationToken = default)
    {
        var searchText = _filterStopWords
            ? StopWordFilter.ExtractKeywords(queryText)
            : queryText;

        if (string.IsNullOrWhiteSpace(searchText))
            return new RetrieverResult([]);

        string cypher = _retrievalQuery is not null
            ? $"""
               CALL db.index.fulltext.queryNodes($index_name, $query)
               YIELD node, score
               WITH node, score
               ORDER BY score DESC
               LIMIT $top_k
               {_retrievalQuery}
               LIMIT $top_k
               """
            : """
              CALL db.index.fulltext.queryNodes($index_name, $query)
              YIELD node, score
              WITH node, score
              ORDER BY score DESC
              LIMIT $top_k
              RETURN node, score
              """;

        var parameters = new Dictionary<string, object?>
        {
            ["index_name"] = _indexName,
            ["query"] = searchText,
            ["top_k"] = topK
        };

        var (records, _, _) = await _driver.ExecutableQuery(cypher)
            .WithParameters(parameters)
            .WithConfig(new QueryConfig(routing: RoutingControl.Readers))
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = records.Select(FormatRecord).ToList();
        return new RetrieverResult(items);
    }

    private static RetrieverResultItem FormatRecord(IRecord record)
    {
        if (record.Keys.Contains("text"))
            return AdapterVectorRetriever.FormatCypherResult(record);

        var node = record["node"].As<INode>();
        var score = record["score"].As<double>();
        var content = node.Properties.TryGetValue("text", out var text)
            ? text?.ToString() ?? ""
            : node.Properties.TryGetValue("content", out var c)
                ? c?.ToString() ?? ""
                : node.ToString()!;
        return new RetrieverResultItem(content, new Dictionary<string, object?> { ["score"] = score });
    }
}
