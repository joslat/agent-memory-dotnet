using Neo4j.AgentMemory.Abstractions.Domain;

namespace Neo4j.AgentMemory.Abstractions.Repositories;

/// <summary>
/// Repository for entity persistence.
/// </summary>
public interface IEntityRepository
{
    /// <summary>
    /// Adds or updates an entity.
    /// </summary>
    Task<Entity> UpsertAsync(
        Entity entity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets an entity by identifier.
    /// </summary>
    Task<Entity?> GetByIdAsync(
        string entityId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets entities by name (exact or alias match).
    /// </summary>
    Task<IReadOnlyList<Entity>> GetByNameAsync(
        string name,
        bool includeAliases = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches entities by vector similarity.
    /// </summary>
    Task<IReadOnlyList<(Entity Entity, double Score)>> SearchByVectorAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets entities by type.
    /// </summary>
    Task<IReadOnlyList<Entity>> GetByTypeAsync(
        string type,
        CancellationToken cancellationToken = default);
}
