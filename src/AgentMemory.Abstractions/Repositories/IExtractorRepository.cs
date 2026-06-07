using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Repositories;

/// <summary>
/// Repository for extractor persistence and EXTRACTED_BY provenance relationships.
/// </summary>
public interface IExtractorRepository
{
    /// <summary>
    /// Creates or updates an extractor node.
    /// </summary>
    Task<Extractor> UpsertAsync(Extractor extractor, CancellationToken ct = default);

    /// <summary>
    /// Gets an extractor by its unique name.
    /// </summary>
    Task<Extractor?> GetByNameAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Lists all registered extractors.
    /// </summary>
    Task<IReadOnlyList<Extractor>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates an EXTRACTED_BY relationship from an entity to an extractor.
    /// </summary>
    Task CreateExtractedByRelationshipAsync(
        string entityId,
        string extractorName,
        double confidence,
        int? extractionTimeMs = null,
        CancellationToken ct = default);

    /// <summary>
    /// Gets entities extracted by a given extractor. Deliberately unscoped (R1): this is an
    /// operator/QA provenance surface keyed by extractor name (a system handle, not a user identity),
    /// intended to span all owners; it has no user-facing caller. See the unscoped-reads disposition in
    /// <c>docs/Memory_Review_and_Implementation_Plan.md</c>.
    /// </summary>
    Task<IReadOnlyList<(Entity Entity, double Confidence)>> GetEntitiesByExtractorAsync(
        string extractorName,
        int limit = 100,
        CancellationToken ct = default);

    /// <summary>
    /// Gets full provenance information for an entity.
    /// </summary>
    Task<EntityProvenance?> GetProvenanceAsync(string entityId, CancellationToken ct = default);

    /// <summary>
    /// Gets aggregate extraction statistics.
    /// </summary>
    Task<ExtractionStats> GetExtractionStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets statistics for a specific extractor.
    /// </summary>
    Task<ExtractorStats?> GetExtractorStatsAsync(string extractorName, CancellationToken ct = default);

    /// <summary>
    /// Deletes all provenance relationships (EXTRACTED_FROM, EXTRACTED_BY) for an entity.
    /// </summary>
    Task<int> DeleteProvenanceAsync(string entityId, CancellationToken ct = default);
}
