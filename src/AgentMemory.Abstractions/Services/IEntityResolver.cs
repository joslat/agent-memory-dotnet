using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Service for resolving and deduplicating entities.
/// </summary>
public interface IEntityResolver
{
    /// <summary>
    /// Resolves an extracted entity to an existing entity, or creates a new one. When
    /// <paramref name="scope"/> is supplied (R1) the candidate set is confined to the owner's own and
    /// (optionally) shared entities — so an incoming entity can never resolve onto (and merge into)
    /// another owner's private entity. Null scope ⇒ unscoped (single-tenant / legacy behavior).
    /// </summary>
    Task<Entity> ResolveEntityAsync(
        ExtractedEntity extractedEntity,
        IReadOnlyList<string> sourceMessageIds,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds potential duplicate entities, optionally confined to an owner/shared <paramref name="scope"/> (R1).
    /// </summary>
    Task<IReadOnlyList<Entity>> FindPotentialDuplicatesAsync(
        string name,
        string type,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);
}
