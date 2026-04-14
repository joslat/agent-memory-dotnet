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

    /// <summary>
    /// Adds or updates a batch of entities atomically.
    /// </summary>
    Task<IReadOnlyList<Entity>> UpsertBatchAsync(
        IReadOnlyList<Entity> entities,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an EXTRACTED_FROM relationship from an entity to a source message.
    /// </summary>
    Task CreateExtractedFromRelationshipAsync(
        string entityId,
        string messageId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches entities within a radius (km) of a geographic point.
    /// Requires the entity_location_idx point index.
    /// </summary>
    Task<IReadOnlyList<Entity>> SearchByLocationAsync(
        double latitude,
        double longitude,
        double radiusKm,
        int limit = 10,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches entities inside an axis-aligned geographic bounding box.
    /// Requires the entity_location_idx point index.
    /// </summary>
    Task<IReadOnlyList<Entity>> SearchInBoundingBoxAsync(
        double minLat,
        double minLon,
        double maxLat,
        double maxLon,
        int limit = 10,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns up to <paramref name="limit"/> entities that have no embedding set.
    /// Used for batch back-fill operations.
    /// </summary>
    Task<IReadOnlyList<Entity>> GetPageWithoutEmbeddingAsync(
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the embedding vector on an existing entity node.
    /// </summary>
    Task UpdateEmbeddingAsync(
        string entityId,
        float[] embedding,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes an entity and all its relationships.</summary>
    Task<bool> DeleteAsync(string entityId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes the search-indexed fields (name, description, aliases) for an entity.
    /// Call after merge operations to ensure fulltext search returns current data.
    /// </summary>
    Task RefreshEntitySearchFieldsAsync(string entityId, CancellationToken cancellationToken = default);
}
