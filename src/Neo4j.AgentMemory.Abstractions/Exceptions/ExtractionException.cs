namespace Neo4j.AgentMemory.Abstractions.Exceptions;

/// <summary>
/// Thrown when the extraction pipeline fails.
/// </summary>
public class ExtractionException : MemoryException
{
    /// <summary>The extraction pipeline step that failed, if applicable.</summary>
    public string? ExtractionStep { get; }

    /// <summary>Initializes a new instance with the specified message.</summary>
    /// <param name="message">The error message.</param>
    public ExtractionException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with the specified message and extraction step.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="extractionStep">The extraction step that failed.</param>
    public ExtractionException(string message, string extractionStep)
        : base(message)
    {
        ExtractionStep = extractionStep;
    }

    /// <summary>Initializes a new instance with the specified message and inner exception.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    public ExtractionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
