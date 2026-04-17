namespace Neo4j.AgentMemory.GraphRagAdapter.Retrieval;

/// <summary>
/// Interface for retrieving search results from Neo4j.
/// </summary>
public interface IRetriever
{
    /// <summary>
    /// Search Neo4j and return matching results.
    /// </summary>
    /// <param name="queryText">The text to search for.</param>
    /// <param name="topK">Maximum number of results to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The search results.</returns>
    Task<RetrieverResult> SearchAsync(string queryText, int topK, CancellationToken cancellationToken = default);
}
