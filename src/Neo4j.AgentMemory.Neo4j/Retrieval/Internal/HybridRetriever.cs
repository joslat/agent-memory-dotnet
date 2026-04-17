using Microsoft.Extensions.AI;
using Neo4j.Driver;

namespace Neo4j.AgentMemory.Neo4j.Retrieval.Internal;

/// <summary>
/// Combined vector + fulltext retriever. Runs both searches concurrently and
/// merges results, taking the highest score for duplicate content.
/// </summary>
internal sealed class HybridRetriever : IRetriever
{
    private readonly VectorRetriever _vector;
    private readonly FulltextRetriever _fulltext;

    internal HybridRetriever(
        IDriver driver,
        string vectorIndexName,
        string fulltextIndexName,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        string? retrievalQuery = null,
        bool filterStopWords = false)
    {
        _vector = new VectorRetriever(driver, vectorIndexName, embeddingGenerator, retrievalQuery);
        _fulltext = new FulltextRetriever(driver, fulltextIndexName, retrievalQuery, filterStopWords);
    }

    public async Task<RetrieverResult> SearchAsync(
        string queryText, int topK, CancellationToken cancellationToken = default)
    {
        var vectorTask = _vector.SearchAsync(queryText, topK, cancellationToken);
        var fulltextTask = _fulltext.SearchAsync(queryText, topK, cancellationToken);

        await Task.WhenAll(vectorTask, fulltextTask).ConfigureAwait(false);

        var vectorResults = await vectorTask.ConfigureAwait(false);
        var fulltextResults = await fulltextTask.ConfigureAwait(false);

        var merged = new Dictionary<string, RetrieverResultItem>();
        foreach (var item in vectorResults.Items.Concat(fulltextResults.Items))
        {
            var key = item.Content;
            if (merged.TryGetValue(key, out var existing))
            {
                if (GetScore(item) > GetScore(existing))
                    merged[key] = item;
            }
            else
            {
                merged[key] = item;
            }
        }

        var items = merged.Values
            .OrderByDescending(GetScore)
            .Take(topK)
            .ToList();

        return new RetrieverResult(items);
    }

    private static double GetScore(RetrieverResultItem item)
    {
        if (item.Metadata?.TryGetValue("score", out var score) == true && score is double d)
            return d;
        return 0;
    }
}
