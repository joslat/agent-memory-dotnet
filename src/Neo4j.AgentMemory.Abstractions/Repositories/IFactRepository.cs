using Neo4j.AgentMemory.Abstractions.Domain;

namespace Neo4j.AgentMemory.Abstractions.Repositories;

/// <summary>
/// Repository for fact persistence.
/// </summary>
public interface IFactRepository
{
    /// <summary>
    /// Adds or updates a fact.
    /// </summary>
    Task<Fact> UpsertAsync(
        Fact fact,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a fact by identifier.
    /// </summary>
    Task<Fact?> GetByIdAsync(
        string factId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets facts by subject.
    /// </summary>
    Task<IReadOnlyList<Fact>> GetBySubjectAsync(
        string subject,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches facts by vector similarity.
    /// </summary>
    Task<IReadOnlyList<(Fact Fact, double Score)>> SearchByVectorAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default);
}
