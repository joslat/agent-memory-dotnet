using Neo4j.AgentMemory.Abstractions.Domain;

namespace Neo4j.AgentMemory.Abstractions.Services;

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
    /// Gets entities by name.
    /// </summary>
    Task<IReadOnlyList<Entity>> GetEntitiesByNameAsync(
        string name,
        bool includeAliases = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches entities semantically.
    /// </summary>
    Task<IReadOnlyList<Entity>> SearchEntitiesAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
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
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches preferences semantically.
    /// </summary>
    Task<IReadOnlyList<Preference>> SearchPreferencesAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
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
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches facts semantically.
    /// </summary>
    Task<IReadOnlyList<Fact>> SearchFactsAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
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
        CancellationToken cancellationToken = default);
}
