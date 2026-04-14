namespace Neo4j.AgentMemory.Abstractions.Exceptions;

/// <summary>
/// Thrown when a Cypher query fails unexpectedly.
/// </summary>
public class GraphQueryException : MemoryException
{
    /// <summary>The Cypher query that failed, if applicable.</summary>
    public string? CypherQuery { get; }

    /// <summary>Initializes a new instance with the specified message.</summary>
    /// <param name="message">The error message.</param>
    public GraphQueryException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with the specified message and Cypher query.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="cypherQuery">The Cypher query that failed.</param>
    public GraphQueryException(string message, string cypherQuery)
        : base(message)
    {
        CypherQuery = cypherQuery;
    }

    /// <summary>Initializes a new instance with the specified message and inner exception.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    public GraphQueryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
