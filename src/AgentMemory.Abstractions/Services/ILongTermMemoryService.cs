using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Service for long-term (structured knowledge) memory operations.
/// </summary>
public interface ILongTermMemoryService
{
    /// <summary>
    /// Adds or updates an entity.
    /// </summary>
    Task<Entity> AddEntityAsync(
        Entity entity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records feedback on an entity by nudging its confidence: <paramref name="positive"/> reinforces,
    /// otherwise it penalizes. The magnitude is <paramref name="delta"/> or, when null, the configured
    /// <c>LongTermMemoryOptions.FeedbackConfidenceDelta</c>; the result is clamped to [0,1]. An optional
    /// <paramref name="scope"/> (R1) restricts the write to the owner's own (or shared) entities so
    /// feedback cannot mutate another owner's private entity. Returns the updated entity, or null if no
    /// entity with that id exists (or it is out of scope).
    /// </summary>
    Task<Entity?> RecordEntityFeedbackAsync(
        string entityId,
        bool positive,
        double? delta = null,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets entities by name.
    /// </summary>
    Task<IReadOnlyList<Entity>> GetEntitiesByNameAsync(
        string name,
        bool includeAliases = true,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches entities semantically.
    /// </summary>
    Task<IReadOnlyList<Entity>> SearchEntitiesAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds or updates a preference.
    /// </summary>
    Task<Preference> AddPreferenceAsync(
        Preference preference,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets preferences by category.
    /// </summary>
    Task<IReadOnlyList<Preference>> GetPreferencesByCategoryAsync(
        string category,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches preferences semantically.
    /// </summary>
    Task<IReadOnlyList<Preference>> SearchPreferencesAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds or updates a fact.
    /// </summary>
    Task<Fact> AddFactAsync(
        Fact fact,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets facts by subject.
    /// </summary>
    Task<IReadOnlyList<Fact>> GetFactsBySubjectAsync(
        string subject,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches facts semantically.
    /// </summary>
    Task<IReadOnlyList<Fact>> SearchFactsAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds or updates a relationship.
    /// </summary>
    Task<Relationship> AddRelationshipAsync(
        Relationship relationship,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets relationships for an entity.
    /// </summary>
    Task<IReadOnlyList<Relationship>> GetEntityRelationshipsAsync(
        string entityId,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a preference by identifier. When <paramref name="scope"/> is supplied (R1) the delete
    /// only affects the owner's own preference — never another owner's, and never shared/global ones.
    /// </summary>
    Task DeletePreferenceAsync(
        string preferenceId,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches entities semantically, returning only those that existed at <paramref name="asOf"/>.
    /// </summary>
    Task<IReadOnlyList<Entity>> SearchEntitiesAsOfAsync(
        float[] queryEmbedding,
        DateTimeOffset asOf,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches facts semantically, returning only those valid at <paramref name="asOf"/>.
    /// </summary>
    Task<IReadOnlyList<Fact>> SearchFactsAsOfAsync(
        float[] queryEmbedding,
        DateTimeOffset asOf,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches preferences semantically, returning only those that existed at <paramref name="asOf"/>.
    /// </summary>
    Task<IReadOnlyList<Preference>> SearchPreferencesAsOfAsync(
        float[] queryEmbedding,
        DateTimeOffset asOf,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);
}
