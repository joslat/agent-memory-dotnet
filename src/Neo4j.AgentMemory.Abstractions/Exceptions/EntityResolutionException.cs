namespace Neo4j.AgentMemory.Abstractions.Exceptions;

/// <summary>
/// Thrown when entity resolution encounters an irrecoverable error.
/// </summary>
public class EntityResolutionException : MemoryException
{
    /// <summary>The entity name that could not be resolved, if applicable.</summary>
    public string? EntityName { get; }

    /// <summary>Initializes a new instance with the specified message.</summary>
    /// <param name="message">The error message.</param>
    public EntityResolutionException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with the specified message and entity name.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="entityName">The entity name that could not be resolved.</param>
    public EntityResolutionException(string message, string entityName)
        : base(message)
    {
        EntityName = entityName;
    }

    /// <summary>Initializes a new instance with the specified message and inner exception.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    public EntityResolutionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
