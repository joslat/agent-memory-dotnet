using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Abstractions.Repositories;

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
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets outgoing relationships from a source entity.
    /// </summary>
    Task<IReadOnlyList<Relationship>> GetBySourceEntityAsync(
        string sourceEntityId,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets incoming relationships to a target entity.
    /// </summary>
    Task<IReadOnlyList<Relationship>> GetByTargetEntityAsync(
        string targetEntityId,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);
}
