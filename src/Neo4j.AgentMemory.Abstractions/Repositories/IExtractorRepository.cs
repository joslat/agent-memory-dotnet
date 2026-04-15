using Neo4j.AgentMemory.Abstractions.Domain;

namespace Neo4j.AgentMemory.Abstractions.Repositories;

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
    /// Gets entities extracted by a given extractor.
    /// </summary>
    Task<IReadOnlyList<(Entity Entity, double Confidence)>> GetEntitiesByExtractorAsync(
        string extractorName,
        int limit = 100,
        CancellationToken ct = default);
}
