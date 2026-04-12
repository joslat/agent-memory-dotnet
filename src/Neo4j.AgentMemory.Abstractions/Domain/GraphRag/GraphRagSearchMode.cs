namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// GraphRAG search modes.
/// </summary>
public enum GraphRagSearchMode
{
    /// <summary>
    /// Vector similarity search.
    /// </summary>
    Vector,

    /// <summary>
    /// Full-text search.
    /// </summary>
    Fulltext,

    /// <summary>
    /// Hybrid vector + full-text search.
    /// </summary>
    Hybrid,

    /// <summary>
    /// Graph traversal-based search.
    /// </summary>
    Graph
}
