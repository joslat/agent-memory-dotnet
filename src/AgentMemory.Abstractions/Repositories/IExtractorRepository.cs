using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Abstractions.Repositories;

/// <summary>
/// Repository for extractor persistence and EXTRACTED_BY provenance relationships.
/// </summary>
public interface IExtractorRepository
{
    /// <summary>
    /// Creates or updates an extractor node.
    /// </summary>
    Task<Extractor> UpsertAsync(Extractor extractor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets an extractor by its unique name.
    /// </summary>
    Task<Extractor?> GetByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all registered extractors.
    /// </summary>
    Task<IReadOnlyList<Extractor>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an EXTRACTED_BY relationship from an entity to an extractor.
    /// </summary>
    Task CreateExtractedByRelationshipAsync(
        string entityId,
        string extractorName,
        double confidence,
        int? extractionTimeMs = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets entities extracted by a given extractor. Deliberately unscoped (R1): this is an
    /// operator/QA provenance surface keyed by extractor name (a system handle, not a user identity),
    /// intended to span all owners; it has no user-facing caller. See the unscoped-reads disposition in
    /// <c>docs/archive/Memory_Review_and_Implementation_Plan.md</c>.
    /// </summary>
    Task<IReadOnlyList<(Entity Entity, double Confidence)>> GetEntitiesByExtractorAsync(
        string extractorName,
        int limit = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets full provenance information for an entity. When <paramref name="scope"/> is supplied (R1)
    /// the lookup is confined to the owner's own or shared entity, returning null when the entity is
    /// out of scope; null scope ⇒ unscoped (admin/audit).
    /// </summary>
    Task<EntityProvenance?> GetProvenanceAsync(string entityId, MemoryScope? scope = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets aggregate extraction statistics.
    /// </summary>
    Task<ExtractionStats> GetExtractionStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets statistics for a specific extractor.
    /// </summary>
    Task<ExtractorStats?> GetExtractorStatsAsync(string extractorName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all provenance relationships (EXTRACTED_FROM, EXTRACTED_BY) for an entity.
    /// </summary>
    Task<int> DeleteProvenanceAsync(string entityId, CancellationToken cancellationToken = default);
}
