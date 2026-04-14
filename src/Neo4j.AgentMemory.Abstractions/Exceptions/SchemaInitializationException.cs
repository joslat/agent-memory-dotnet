namespace Neo4j.AgentMemory.Abstractions.Exceptions;

/// <summary>
/// Thrown when schema bootstrap fails.
/// </summary>
public class SchemaInitializationException : MemoryException
{
    /// <summary>The schema operation that failed, if applicable.</summary>
    public string? SchemaOperation { get; }

    /// <summary>Initializes a new instance with the specified message.</summary>
    /// <param name="message">The error message.</param>
    public SchemaInitializationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with the specified message and schema operation.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="schemaOperation">The schema operation that failed.</param>
    public SchemaInitializationException(string message, string schemaOperation)
        : base(message)
    {
        SchemaOperation = schemaOperation;
    }

    /// <summary>Initializes a new instance with the specified message and inner exception.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    public SchemaInitializationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
