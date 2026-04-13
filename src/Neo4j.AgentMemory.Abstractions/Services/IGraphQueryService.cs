namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Service for executing raw graph queries.
/// </summary>
public interface IGraphQueryService
{
    /// <summary>
    /// Executes a Cypher query and returns the results as dictionaries.
    /// </summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
        string cypherQuery,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default);
}
