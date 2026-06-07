using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Abstractions.Repositories;

/// <summary>
/// Repository for preference persistence.
/// </summary>
public interface IPreferenceRepository
{
    /// <summary>Adds or updates a preference.</summary>
    Task<Preference> UpsertAsync(Preference preference, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the most-similar existing preference in <paramref name="category"/> within the same owner
    /// (<paramref name="ownerId"/>; null = shared) whose cosine score ≥ <paramref name="threshold"/>,
    /// or null. Used for dedup-on-create.
    /// </summary>
    Task<Preference?> FindDuplicateAsync(
        string category, float[] embedding, string? ownerId, double threshold,
        CancellationToken cancellationToken = default);

    /// <summary>Reinforces an existing preference reached by dedup: sets its confidence and returns it.</summary>
    Task<Preference> MarkDeduplicatedAsync(string preferenceId, double confidence, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a preference by identifier. Deliberately unscoped (R1): the id is itself an already-owned
    /// handle, so no owner filter is applied. See the unscoped-reads disposition in
    /// <c>docs/Memory_Review_and_Implementation_Plan.md</c>.
    /// </summary>
    Task<Preference?> GetByIdAsync(string preferenceId, CancellationToken cancellationToken = default);

    /// <summary>Gets preferences by category.</summary>
    Task<IReadOnlyList<Preference>> GetByCategoryAsync(string category, MemoryScope? scope = null, CancellationToken cancellationToken = default);

    /// <summary>Searches preferences by vector similarity.</summary>
    Task<IReadOnlyList<(Preference Preference, double Score)>> SearchByVectorAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a preference by identifier. When <paramref name="scope"/> is supplied (R1) the delete
    /// only affects the owner's own preference — never another owner's, and never shared/global ones.
    /// </summary>
    Task DeleteAsync(string preferenceId, MemoryScope? scope = null, CancellationToken cancellationToken = default);

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
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);
}
