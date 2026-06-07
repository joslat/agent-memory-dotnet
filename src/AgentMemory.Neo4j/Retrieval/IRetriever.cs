namespace AgentMemory.Neo4j.Retrieval;

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
    /// <param name="ownerId">
    /// Optional owner/user id (R1). When supplied, results are restricted to that owner's nodes plus
    /// shared/global nodes (<c>owner_id IS NULL</c>). Null = unscoped (today's behavior).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The search results.</returns>
    Task<RetrieverResult> SearchAsync(
        string queryText, int topK, string? ownerId = null, CancellationToken cancellationToken = default);
}
