using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Service for long-term (structured knowledge) memory operations.
/// </summary>
/// <remarks>
/// <para><b>Lifecycle surface (intentionally asymmetric).</b> The library follows an
/// <i>invalidate-not-delete</i> philosophy, so the canonical way to close any long-term node is
/// <c>Invalidate*</c> (non-destructive, reversible, owner-scoped) — available for entities, facts, and
/// preferences alike.</para>
/// <list type="bullet">
///   <item><b>Supersession</b> (<c>Supersede*</c>) is offered for <b>facts and preferences only</b>,
///   mirroring the <c>:SUPERSEDED_BY</c> edge (Fact→Fact / Preference→Preference). Entities are closed via
///   <c>InvalidateEntityAsync</c>, not superseded.</item>
///   <item><b>Hard delete</b> is exposed here only as <c>DeletePreferenceAsync</c>; destructive removal is
///   deliberately kept off this service for facts/entities. For an explicit policy delete (GDPR / TTL),
///   call the repository <c>DeleteAsync</c> directly — but prefer <c>Invalidate*</c> in normal use.</item>
/// </list>
/// </remarks>
public interface ILongTermMemoryService
{
    /// <summary>
    /// Adds or updates an entity. When the entity's <c>Confidence</c> is below
    /// <c>LongTermMemoryOptions.MinConfidenceThreshold</c> the add is skipped (not persisted) and the
    /// supplied entity is returned unchanged.
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
    /// Adds or updates a preference. When the preference's <c>Confidence</c> is below
    /// <c>LongTermMemoryOptions.MinConfidenceThreshold</c> the add is skipped (not persisted) and the
    /// supplied preference is returned unchanged.
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
    /// Adds or updates a fact. When the fact's <c>Confidence</c> is below
    /// <c>LongTermMemoryOptions.MinConfidenceThreshold</c> the add is skipped (not persisted) and the
    /// supplied fact is returned unchanged.
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
    /// Bitemporal fact search (D6): <paramref name="asOf"/> is the valid-time clock ("what was true");
    /// <paramref name="systemAsOf"/> is the transaction-time clock ("what we believed"), defaulting to
    /// <paramref name="asOf"/> for ordinary single-clock point-in-time recall.
    /// </summary>
    Task<IReadOnlyList<Fact>> SearchFactsAsOfAsync(
        float[] queryEmbedding,
        DateTimeOffset asOf,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        DateTimeOffset? systemAsOf = null,
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

    // ── Invalidation & supersession (D5 / D7) ───────────────────────────

    /// <summary>
    /// Soft-invalidates a fact by id (D5 transaction clock): it leaves live recall but is retained and
    /// stays visible to as-of recall for times before invalidation. Owner-scoped (R1) when
    /// <paramref name="scope"/> is set. Idempotent. Returns true if a matching fact existed in scope.
    /// </summary>
    Task<bool> InvalidateFactAsync(string factId, MemoryScope? scope = null, CancellationToken cancellationToken = default);

    /// <summary>Soft-invalidates an entity by id (D5). See <see cref="InvalidateFactAsync"/>.</summary>
    Task<bool> InvalidateEntityAsync(string entityId, MemoryScope? scope = null, CancellationToken cancellationToken = default);

    /// <summary>Soft-invalidates a preference by id (D5). See <see cref="InvalidateFactAsync"/>.</summary>
    Task<bool> InvalidatePreferenceAsync(string preferenceId, MemoryScope? scope = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Supersedes the <paramref name="loserFactId"/> with the <paramref name="winnerFactId"/> (D7): closes
    /// the loser non-destructively (<c>invalidated_at</c> + <c>valid_until</c>) and links
    /// <c>(loser)-[:SUPERSEDED_BY]-&gt;(winner)</c>. Owner-scoped (R1) — both facts must belong to the
    /// owner. Idempotent. Returns true if a matching loser+winner existed in scope.
    /// </summary>
    Task<bool> SupersedeFactAsync(string loserFactId, string winnerFactId, MemoryScope? scope = null, CancellationToken cancellationToken = default);

    /// <summary>Supersedes the loser preference with the winner (D7). See <see cref="SupersedeFactAsync"/>.</summary>
    Task<bool> SupersedePreferenceAsync(string loserPreferenceId, string winnerPreferenceId, MemoryScope? scope = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fact recall with optional canonical-predicate expansion — a relation returned <b>whole</b>.
    /// </summary>
    /// <remarks>
    /// A default interface method, not extra optional parameters on the method above: adding optional
    /// parameters to a published interface breaks every implementor. The default ignores expansion,
    /// so a store that cannot retrieve by relation behaves exactly as before.
    /// </remarks>
    Task<IReadOnlyList<Fact>> SearchFactsAsync(
        float[] queryEmbedding,
        int limit,
        double minScore,
        MemoryScope? scope,
        bool expandByPredicate,
        int expansionLimit,
        CancellationToken cancellationToken) =>
        SearchFactsAsync(queryEmbedding, limit, minScore, scope, cancellationToken);

    /// <summary>
    /// Fact recall with expansion driven by the relations the question itself names.
    /// </summary>
    /// <remarks>
    /// Expansion alone can only widen predicates that similarity already surfaced in the top-K, so a
    /// question naming several relations reaches only whichever of them retrieval happened to nominate.
    /// <paramref name="questionRelations"/> supplies them from the question instead. An empty list
    /// reproduces the previous overload exactly, which is what keeps this from ever being worse than
    /// the existing behaviour.
    /// <para>
    /// A further default interface method for the same reason as the one above: adding optional
    /// parameters to a published interface breaks every implementor, and the interface is locked
    /// under SemVer.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<Fact>> SearchFactsAsync(
        float[] queryEmbedding,
        int limit,
        double minScore,
        MemoryScope? scope,
        bool expandByPredicate,
        int expansionLimit,
        IReadOnlyList<string> questionRelations,
        CancellationToken cancellationToken) =>
        SearchFactsAsync(
            queryEmbedding, limit, minScore, scope, expandByPredicate, expansionLimit,
            cancellationToken);


    /// <summary>
    /// Fact recall restricted to facts whose valid-time window contains the present.
    /// </summary>
    /// <remarks>
    /// A third default interface method, for the same reason as the two above: the interface is locked
    /// under SemVer and optional parameters would break every implementor. The default ignores
    /// <paramref name="validTime"/>, which is what every implementation does today — a store that
    /// cannot filter on valid time keeps working and simply does not filter, rather than appearing to.
    /// </remarks>
    Task<IReadOnlyList<Fact>> SearchFactsAsync(
        float[] queryEmbedding,
        ValidTimeMode validTime,
        int limit,
        double minScore,
        MemoryScope? scope,
        CancellationToken cancellationToken) =>
        SearchFactsAsync(queryEmbedding, limit, minScore, scope, cancellationToken);
}
