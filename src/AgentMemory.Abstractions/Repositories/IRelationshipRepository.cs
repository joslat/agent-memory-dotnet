using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Abstractions.Repositories;

/// <summary>
/// Repository for relationship persistence.
/// </summary>
public interface IRelationshipRepository
{
    /// <summary>
    /// Adds or updates a relationship.
    /// </summary>
    Task<Relationship> UpsertAsync(
        Relationship relationship,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a relationship by identifier.
    /// </summary>
    Task<Relationship?> GetByIdAsync(
        string relationshipId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets relationships for an entity (source or target).
    /// </summary>
    Task<IReadOnlyList<Relationship>> GetByEntityAsync(
        string entityId,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets outgoing relationships from a source entity.
    /// </summary>
    Task<IReadOnlyList<Relationship>> GetBySourceEntityAsync(
        string sourceEntityId,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets incoming relationships to a target entity.
    /// </summary>
    Task<IReadOnlyList<Relationship>> GetByTargetEntityAsync(
        string targetEntityId,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 36.7. The live relationships touching any of <paramref name="entityIds"/> (either end), with both entities'
    /// names, most confident first, at most <paramref name="limit"/>. Live means not ended: no <c>valid_until</c>, or
    /// one after <paramref name="now"/>.
    /// </summary>
    /// <remarks>
    /// What recall hands the agent about how the recalled people and things relate ("Carmen is my best friend").
    /// The default implementation throws <see cref="NotSupportedException"/>, so existing implementations compile;
    /// recall then renders no relationships.
    /// </remarks>
    Task<IReadOnlyList<RecalledRelationship>> GetLiveAmongAsync(
        IReadOnlyList<string> entityIds,
        int limit,
        DateTimeOffset now,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This IRelationshipRepository implementation cannot read relationships for recall.");

    /// <summary>
    /// 36.4. Ends a relationship: sets its <c>valid_until</c> to <paramref name="endedAt"/> unless it already ended
    /// (the first end is kept). The edge stays, with its provenance, for history. Returns true when it exists in
    /// <paramref name="scope"/>.
    /// </summary>
    /// <remarks>
    /// How a single-valued relation ("lives in") is replaced: a new residence ends the old edge instead of standing
    /// beside it. The default implementation throws <see cref="NotSupportedException"/>.
    /// </remarks>
    Task<bool> EndAsync(
        string relationshipId,
        DateTimeOffset endedAt,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This IRelationshipRepository implementation cannot end a relationship.");
}
