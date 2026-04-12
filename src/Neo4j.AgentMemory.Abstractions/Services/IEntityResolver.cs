using Neo4j.AgentMemory.Abstractions.Domain;

namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Service for resolving and deduplicating entities.
/// </summary>
public interface IEntityResolver
{
    /// <summary>
    /// Resolves an extracted entity to an existing entity, or creates a new one.
    /// </summary>
    Task<Entity> ResolveEntityAsync(
        ExtractedEntity extractedEntity,
        IReadOnlyList<string> sourceMessageIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds potential duplicate entities.
    /// </summary>
    Task<IReadOnlyList<Entity>> FindPotentialDuplicatesAsync(
        string name,
        string type,
        CancellationToken cancellationToken = default);
}
