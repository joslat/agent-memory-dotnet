namespace Neo4j.AgentMemory.Abstractions.Exceptions;

/// <summary>
/// Base exception for all memory system errors.
/// </summary>
public class MemoryException : Exception
{
    /// <summary>Structured error code (e.g. <see cref="MemoryErrorCodes.EntityNotFound"/>). Null when constructed without a builder.</summary>
    public string? Code { get; }

    /// <summary>Structured key/value metadata attached by <see cref="MemoryErrorBuilder"/>. Empty when constructed without a builder.</summary>
    public IReadOnlyDictionary<string, object?> Metadata { get; }

    /// <summary>Initializes a new instance with the specified message.</summary>
    /// <param name="message">The error message.</param>
    public MemoryException(string message) : base(message)
    {
        Metadata = new Dictionary<string, object?>();
    }

    /// <summary>Initializes a new instance with the specified message and inner exception.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    public MemoryException(string message, Exception innerException) : base(message, innerException)
    {
        Metadata = new Dictionary<string, object?>();
    }

    /// <summary>Internal constructor used exclusively by <see cref="MemoryErrorBuilder"/>.</summary>
    internal MemoryException(string message, string? code, IReadOnlyDictionary<string, object?> metadata, Exception? inner)
        : base(message, inner)
    {
        Code = code;
        Metadata = metadata;
    }
}
