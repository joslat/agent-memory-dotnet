using Neo4j.AgentMemory.Abstractions.Domain;

namespace Neo4j.AgentMemory.Abstractions.Repositories;

/// <summary>
/// Repository for preference persistence.
/// </summary>
public interface IPreferenceRepository
{
    /// <summary>Adds or updates a preference.</summary>
    Task<Preference> UpsertAsync(Preference preference, CancellationToken cancellationToken = default);

    /// <summary>Gets a preference by identifier.</summary>
    Task<Preference?> GetByIdAsync(string preferenceId, CancellationToken cancellationToken = default);

    /// <summary>Gets preferences by category.</summary>
    Task<IReadOnlyList<Preference>> GetByCategoryAsync(string category, CancellationToken cancellationToken = default);

    /// <summary>Searches preferences by vector similarity.</summary>
    Task<IReadOnlyList<(Preference Preference, double Score)>> SearchByVectorAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a preference by identifier.</summary>
    Task DeleteAsync(string preferenceId, CancellationToken cancellationToken = default);

    /// <summary>Creates an EXTRACTED_FROM relationship from a preference to a source message.</summary>
    Task CreateExtractedFromRelationshipAsync(string preferenceId, string messageId, CancellationToken cancellationToken = default);

    /// <summary>Creates an ABOUT relationship from a preference to an entity.</summary>
    Task CreateAboutRelationshipAsync(string preferenceId, string entityId, CancellationToken cancellationToken = default);

    /// <summary>Creates a HAS_PREFERENCE relationship from a conversation to this preference.</summary>
    Task CreateConversationPreferenceRelationshipAsync(string conversationId, string preferenceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns up to <paramref name="limit"/> preferences that have no embedding set.
    /// Used for batch back-fill operations.  Uses the N+1 pattern so callers can
    /// detect a next page without an extra COUNT(*) round-trip.
    /// </summary>
    Task<PagedResult<Preference>> GetPageWithoutEmbeddingAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>Sets the embedding vector on an existing preference node.</summary>
    Task UpdateEmbeddingAsync(string preferenceId, float[] embedding, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches preferences by vector similarity, returning only those that existed at <paramref name="asOf"/>.
    /// </summary>
    Task<IReadOnlyList<(Preference Preference, double Score)>> SearchByVectorAsOfAsync(
        float[] queryEmbedding,
        DateTimeOffset asOf,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default);
}
