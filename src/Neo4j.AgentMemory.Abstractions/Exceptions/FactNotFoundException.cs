namespace Neo4j.AgentMemory.Abstractions.Exceptions;

/// <summary>
/// Thrown when a fact lookup by ID returns null.
/// </summary>
public class FactNotFoundException : MemoryException
{
    /// <summary>The ID of the fact that was not found.</summary>
    public string FactId { get; }

    /// <summary>Initializes a new instance for the specified fact ID.</summary>
    /// <param name="factId">The fact ID that was not found.</param>
    public FactNotFoundException(string factId)
        : base($"Fact not found: {factId}")
    {
        FactId = factId;
    }

    /// <summary>Initializes a new instance for the specified fact ID with an inner exception.</summary>
    /// <param name="factId">The fact ID that was not found.</param>
    /// <param name="innerException">The inner exception.</param>
    public FactNotFoundException(string factId, Exception innerException)
        : base($"Fact not found: {factId}", innerException)
    {
        FactId = factId;
    }
}
