using Microsoft.Extensions.AI;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Retrieval.Internal;

/// <summary>
/// Vector similarity retriever backed by a Neo4j vector index.
/// </summary>
internal sealed class VectorRetriever : IRetriever
{
    private readonly IDriver _driver;
    private readonly string _indexName;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly string? _retrievalQuery;

    internal VectorRetriever(
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
        string queryText, int topK, string? ownerId = null, CancellationToken cancellationToken = default)
    {
        var embedding = await _embeddingGenerator
            .GenerateVectorAsync(queryText, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        bool scoped = RetrieverScope.IsScoped(ownerId);
        string ownerWhere = RetrieverScope.OwnerWhere("node", scoped);

        // Over-fetch ($fetchK) then owner-filter then LIMIT $k, so a scoped query is not starved by
        // higher-scoring foreign rows. When unscoped, $fetchK == $k and ownerWhere is a blank line.
        string cypher = _retrievalQuery is not null
            ? $"""
               CALL db.index.vector.queryNodes($index, $fetchK, $embedding)
               YIELD node, score
               {ownerWhere}
               WITH node, score
               ORDER BY score DESC
               LIMIT $k
               {_retrievalQuery}
               LIMIT $k
               """
            : $"""
              CALL db.index.vector.queryNodes($index, $fetchK, $embedding)
              YIELD node, score
              {ownerWhere}
              WITH node, score
              ORDER BY score DESC
              LIMIT $k
              RETURN node, score
              """;

        var parameters = new Dictionary<string, object?>
        {
            ["index"] = _indexName,
            ["k"] = topK,
            ["fetchK"] = RetrieverScope.FetchK(topK, scoped),
            ["embedding"] = embedding.ToArray()
        };
        if (scoped) parameters["owner_id"] = ownerId;

        var (records, _, _) = await _driver.ExecutableQuery(cypher)
            .WithParameters(parameters)
            .WithConfig(new QueryConfig(routing: RoutingControl.Readers))
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = records.Select(r =>
            _retrievalQuery is not null
                ? FormatCypherResult(r)
                : RetrieverRecordMapper.FromNodeScore(r)
        ).ToList();

        return new RetrieverResult(items);
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
