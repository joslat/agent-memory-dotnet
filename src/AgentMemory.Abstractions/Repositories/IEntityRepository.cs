using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Abstractions.Repositories;

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
    /// Gets an entity by identifier. Deliberately unscoped (R1): the id is itself an already-owned
    /// handle (a caller can only hold it via an owner-scoped recall or by having created the entity),
    /// so no owner filter is applied. See the unscoped-reads disposition in
    /// <c>docs/archive/Memory_Review_and_Implementation_Plan.md</c>.
    /// </summary>
    Task<Entity?> GetByIdAsync(
        string entityId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adjusts an entity's confidence by <paramref name="delta"/> (clamped to [0,1]) and stamps its
    /// update time. Honors an optional owner/shared <paramref name="scope"/> (R1) so feedback cannot
    /// mutate another owner's private entity. Returns the updated entity, or null if no entity with that
    /// id exists (or it is out of scope). Backs the entity-feedback (reinforce/penalize) surface.
    /// </summary>
    Task<Entity?> ApplyConfidenceDeltaAsync(
        string entityId,
        double delta,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets entities by name (exact or alias match).
    /// </summary>
    Task<IReadOnlyList<Entity>> GetByNameAsync(
        string name,
        bool includeAliases = true,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches entities by vector similarity.
    /// </summary>
    Task<IReadOnlyList<(Entity Entity, double Score)>> SearchByVectorAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets entities by type. When <paramref name="scope"/> is supplied (R1) only the owner's own and
    /// (optionally) shared entities are returned — used by entity resolution so one owner's incoming
    /// entity cannot resolve onto another owner's private entity. Null scope ⇒ unscoped.
    /// </summary>
    Task<IReadOnlyList<Entity>> GetByTypeAsync(
        string type,
        MemoryScope? scope = null,
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
        double? confidence = null,
        int? startPos = null,
        int? endPos = null,
        string? context = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches entities within a radius (km) of a geographic point, optionally owner-scoped (R1).
    /// Requires the entity_location_idx point index.
    /// </summary>
    Task<IReadOnlyList<Entity>> SearchByLocationAsync(
        double latitude,
        double longitude,
        double radiusKm,
        int limit = 10,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches entities inside an axis-aligned geographic bounding box, optionally owner-scoped (R1).
    /// Requires the entity_location_idx point index.
    /// </summary>
    Task<IReadOnlyList<Entity>> SearchInBoundingBoxAsync(
        double minLat,
        double minLon,
        double maxLat,
        double maxLon,
        int limit = 10,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns up to <paramref name="limit"/> entities that have no embedding set.
    /// Used for batch back-fill operations.  Uses the N+1 pattern so callers can
    /// detect a next page without an extra COUNT(*) round-trip.
    /// </summary>
    Task<PagedResult<Entity>> GetPageWithoutEmbeddingAsync(
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the embedding vector on an existing entity node.
    /// </summary>
    Task UpdateEmbeddingAsync(
        string entityId,
        float[] embedding,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an entity and all its relationships. When <paramref name="scope"/> is supplied (R1) the
    /// delete only affects the owner's own entity — never another owner's, and never shared/global ones.
    /// </summary>
    Task<bool> DeleteAsync(string entityId, MemoryScope? scope = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-invalidates an entity by id (D5 transaction clock): stamps <c>invalidated_at</c> so it leaves
    /// live recall but is retained (auditable, recoverable, visible to as-of recall before invalidation).
    /// Owner-scoped (R1) when <paramref name="scope"/> is set. Idempotent. Returns true if a matching
    /// entity existed in scope.
    /// </summary>
    Task<bool> InvalidateAsync(string entityId, MemoryScope? scope = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Merges a source (duplicate) entity into a target (canonical) entity: transfers the source's
    /// <c>MENTIONS</c> and <c>SAME_AS</c> edges to the target, folds the source's name/aliases and
    /// description into the target, stamps <c>merged_into</c>/<c>merged_at</c> on the source, and clears
    /// the target's embedding for re-derivation. When <paramref name="scope"/> is supplied (R1) BOTH
    /// endpoints must be the owner's own (or shared) — a merge can never reach across the isolation
    /// boundary into another owner's entity. A cross-boundary (or otherwise non-matching) call is a true
    /// no-op: it matches no rows and leaves both entities untouched, including skipping the post-merge
    /// search-field refresh, so a scoped caller never bumps another owner's entity. Null scope ⇒ unscoped
    /// (admin/maintenance dedup). Note: arbitrary typed relationships (other than SAME_AS/MENTIONS) are
    /// not yet re-pointed from source to target — see the merge-relationship-transfer follow-up.
    /// Returns <c>true</c> if the merge matched and ran; <c>false</c> for a guarded / non-existent no-op.
    /// (The return value future-proofs the relationship-transfer follow-up, which will report edges moved.)
    /// </summary>
    Task<bool> MergeEntitiesAsync(
        string sourceEntityId,
        string targetEntityId,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes the search-indexed fields (name, description, aliases) for an entity.
    /// Call after merge operations to ensure fulltext search returns current data.
    /// </summary>
    Task RefreshEntitySearchFieldsAsync(string entityId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds entities similar to the given entity by embedding vector similarity. When
    /// <paramref name="scope"/> is supplied (R1) the results are confined to the owner's own and
    /// (optionally) shared entities. Null scope ⇒ unscoped (admin/maintenance dedup).
    /// </summary>
    Task<IReadOnlyList<(Entity Entity, double Similarity)>> FindSimilarByEmbeddingAsync(
        string entityId, double minSimilarity = 0.85, int limit = 10, MemoryScope? scope = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets pending SAME_AS duplicate pairs for manual review. Deliberately unscoped (R1): this is an
    /// admin/maintenance dedup-review surface intended to span all owners; it has no user-facing caller.
    /// </summary>
    Task<IReadOnlyList<DuplicatePair>> GetPendingDuplicatesAsync(int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets aggregate SAME_AS relationship counts grouped by status.
    /// </summary>
    Task<DeduplicationStats> GetDeduplicationStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all entities extracted from a specific message. Deliberately unscoped (R1): the result is
    /// already confined by <paramref name="messageId"/> (itself an owned handle), so no owner filter is added.
    /// </summary>
    Task<IReadOnlyList<Entity>> GetEntitiesFromMessageAsync(string messageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches entities by vector similarity, returning only those that existed at <paramref name="asOf"/>.
    /// </summary>
    Task<IReadOnlyList<(Entity Entity, double Score)>> SearchByVectorAsOfAsync(
        float[] queryEmbedding,
        DateTimeOffset asOf,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);
}
