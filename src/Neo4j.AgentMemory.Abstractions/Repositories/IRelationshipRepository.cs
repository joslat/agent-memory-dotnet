using Neo4j.AgentMemory.Abstractions.Domain;

namespace Neo4j.AgentMemory.Abstractions.Repositories;

/// <summary>
/// Repository for relationship persistence.
/// </summary>
public interface IRelationshipRepository
{
    /// <summary>
    /// Adds or updates a relationship.
    /// </summary>
    Task<Relationship> UpsertAsync(
        Relationship relationship,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a relationship by identifier.
    /// </summary>
    Task<Relationship?> GetByIdAsync(
        string relationshipId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets relationships for an entity (source or target).
    /// </summary>
    Task<IReadOnlyList<Relationship>> GetByEntityAsync(
        string entityId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets outgoing relationships from a source entity.
    /// </summary>
    Task<IReadOnlyList<Relationship>> GetBySourceEntityAsync(
        string sourceEntityId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets incoming relationships to a target entity.
    /// </summary>
    Task<IReadOnlyList<Relationship>> GetByTargetEntityAsync(
        string targetEntityId,
        CancellationToken cancellationToken = default);
}
