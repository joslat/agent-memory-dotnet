using Microsoft.Extensions.AI;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Retrieval.Internal;

/// <summary>
/// Combined vector + fulltext retriever. Runs both searches concurrently and fuses the two ranked lists
/// with Reciprocal Rank Fusion (RRF) — a scale-free combiner that avoids comparing raw cosine ([0,1]) and
/// BM25 (unbounded) scores directly (which would let keyword frequency dominate semantic relevance).
/// </summary>
internal sealed class HybridRetriever : IRetriever
{
    // RRF rank-bias constant (Cormack et al. 2009). Larger k flattens the contribution of top ranks.
    private const int RrfK = 60;

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
        string queryText, int topK, string? ownerId = null, CancellationToken cancellationToken = default)
    {
        var vectorTask = _vector.SearchAsync(queryText, topK, ownerId, cancellationToken);
        var fulltextTask = _fulltext.SearchAsync(queryText, topK, ownerId, cancellationToken);

        await Task.WhenAll(vectorTask, fulltextTask).ConfigureAwait(false);

        var vectorResults = await vectorTask.ConfigureAwait(false);
        var fulltextResults = await fulltextTask.ConfigureAwait(false);

        var items = FuseReciprocalRank(vectorResults.Items, fulltextResults.Items, topK);
        return new RetrieverResult(items);
    }

    /// <summary>
    /// Fuses two already-ranked result lists with Reciprocal Rank Fusion: each item contributes
    /// <c>1 / (k + rank)</c> (1-based rank within its own list) to its content's combined score, and the
    /// items are ordered by the summed score. Items appearing in both lists are reinforced. This compares
    /// the lists by RANK, not by their incomparable raw score scales.
    /// </summary>
    internal static List<RetrieverResultItem> FuseReciprocalRank(
        IReadOnlyList<RetrieverResultItem> vectorItems,
        IReadOnlyList<RetrieverResultItem> fulltextItems,
        int topK)
    {
        var scores = new Dictionary<string, double>();
        var representative = new Dictionary<string, RetrieverResultItem>();

        void Accumulate(IReadOnlyList<RetrieverResultItem> items)
        {
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var key = item.Content;
                var rrf = 1.0 / (RrfK + i + 1); // i is 0-based ⇒ rank = i + 1
                scores[key] = scores.TryGetValue(key, out var prev) ? prev + rrf : rrf;
                representative.TryAdd(key, item); // first occurrence (vector before fulltext) wins as representative
            }
        }

        Accumulate(vectorItems);
        Accumulate(fulltextItems);

        return scores
            .OrderByDescending(kv => kv.Value)
            .Take(topK)
            .Select(kv => representative[kv.Key])
            .ToList();
    }
}
