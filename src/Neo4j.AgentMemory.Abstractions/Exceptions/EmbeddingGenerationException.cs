namespace Neo4j.AgentMemory.Abstractions.Exceptions;

/// <summary>
/// Thrown when embedding generation fails.
/// </summary>
public class EmbeddingGenerationException : MemoryException
{
    /// <summary>The input text that failed to generate an embedding, if applicable.</summary>
    public string? InputText { get; }

    /// <summary>Initializes a new instance with the specified message.</summary>
    /// <param name="message">The error message.</param>
    public EmbeddingGenerationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with the specified message and input text.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="inputText">The text that failed to embed.</param>
    public EmbeddingGenerationException(string message, string inputText)
        : base(message)
    {
        InputText = inputText;
    }

    /// <summary>Initializes a new instance with the specified message and inner exception.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    public EmbeddingGenerationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
