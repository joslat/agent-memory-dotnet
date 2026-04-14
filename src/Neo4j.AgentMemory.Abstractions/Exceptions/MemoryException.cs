namespace Neo4j.AgentMemory.Abstractions.Exceptions;

/// <summary>
/// Base exception for all memory system errors.
/// </summary>
public class MemoryException : Exception
{
    /// <summary>Initializes a new instance with the specified message.</summary>
    /// <param name="message">The error message.</param>
    public MemoryException(string message) : base(message) { }

    /// <summary>Initializes a new instance with the specified message and inner exception.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    public MemoryException(string message, Exception innerException) : base(message, innerException) { }
}
