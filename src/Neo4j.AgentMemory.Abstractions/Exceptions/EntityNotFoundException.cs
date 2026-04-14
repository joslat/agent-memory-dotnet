namespace Neo4j.AgentMemory.Abstractions.Exceptions;

/// <summary>
/// Thrown when an entity lookup by ID returns null.
/// </summary>
public class EntityNotFoundException : MemoryException
{
    /// <summary>The ID of the entity that was not found.</summary>
    public string EntityId { get; }

    /// <summary>Initializes a new instance for the specified entity ID.</summary>
    /// <param name="entityId">The entity ID that was not found.</param>
    public EntityNotFoundException(string entityId)
        : base($"Entity not found: {entityId}")
    {
        EntityId = entityId;
    }

    /// <summary>Initializes a new instance for the specified entity ID with an inner exception.</summary>
    /// <param name="entityId">The entity ID that was not found.</param>
    /// <param name="innerException">The inner exception.</param>
    public EntityNotFoundException(string entityId, Exception innerException)
        : base($"Entity not found: {entityId}", innerException)
    {
        EntityId = entityId;
    }
}
