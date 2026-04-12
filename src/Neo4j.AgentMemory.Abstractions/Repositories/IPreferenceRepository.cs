using Neo4j.AgentMemory.Abstractions.Domain;

namespace Neo4j.AgentMemory.Abstractions.Repositories;

/// <summary>
/// Repository for preference persistence.
/// </summary>
public interface IPreferenceRepository
{
    /// <summary>
    /// Adds or updates a preference.
    /// </summary>
    Task<Preference> UpsertAsync(
        Preference preference,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a preference by identifier.
    /// </summary>
    Task<Preference?> GetByIdAsync(
        string preferenceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets preferences by category.
    /// </summary>
    Task<IReadOnlyList<Preference>> GetByCategoryAsync(
        string category,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches preferences by vector similarity.
    /// </summary>
    Task<IReadOnlyList<(Preference Preference, double Score)>> SearchByVectorAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default);
}
