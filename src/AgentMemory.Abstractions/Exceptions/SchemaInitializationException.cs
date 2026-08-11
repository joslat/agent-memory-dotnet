namespace AgentMemory.Abstractions.Exceptions;

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

    /// <summary>
    /// Initializes a new instance carrying both the failing schema operation and a structured
    /// <see cref="MemoryException.Code"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="MemoryException.Code"/> is set only by the assembly-internal builder constructor,
    /// and <c>MemoryError.Create(...).Build()</c> returns the base type — so without this overload a
    /// schema failure could be typed or coded, never both. Worse, the two-argument overload accepts a
    /// code positionally and files it under <see cref="SchemaOperation"/>, leaving <c>Code</c> null
    /// with nothing to notice at the call site. Taking both explicitly removes the ambiguity.
    /// </remarks>
    /// <param name="message">The error message.</param>
    /// <param name="schemaOperation">The schema operation that failed.</param>
    /// <param name="code">A structured code from <see cref="MemoryErrorCodes"/>.</param>
    public SchemaInitializationException(string message, string schemaOperation, string code)
        : base(message, code, new Dictionary<string, object?>(), inner: null)
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
