using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Repositories;

/// <summary>
/// Optional atomic decision/binding storage. Implementations preserve source observations and keep
/// pending proposals separate from legacy equality. Null owner means shared-only, never unscoped.
/// </summary>
public interface IIdentityDecisionRepository
{
    /// <summary>Whether the owned schema and atomic decision capability were explicitly enabled.</summary>
    bool IsEnabled { get; }

    /// <summary>Whether repository calls inside a scoped ingestion can commit together or roll back together.</summary>
    bool SupportsAtomicIngestion => false;
    /// <summary>Whether a caller-owned write transaction would prevent this ingestion owning its commit boundary.</summary>
    bool HasActiveWriteTransaction => false;

    /// <summary>
    /// Runs one ingestion atomically, locking its identity owner/shared scopes before any repository write.
    /// All storage calls made by work must join this transaction. Model calls belong outside this callback.
    /// A lost commit acknowledgement requires retrying stable observation keys; it does not imply rollback.
    /// </summary>
    Task<T> ExecuteScopedAsync<T>(string? ownerId, bool includeShared, IReadOnlyList<string> sourceMessageIds,
        Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This identity backend does not support atomic scoped ingestion.");

    /// <summary>
    /// Persist an initial decision and its fact-role support atomically, or replay its stable key.
    /// Candidate evidence and target scope are revalidated at the write boundary. Replays preserve
    /// reviewed state. The durable outcome is all-or-none. A lost commit acknowledgement may leave the complete operation stored; retry its stable key to discover that outcome without duplicating support.
    /// </summary>
    Task<IdentityDecision> SaveAsync(IdentityDecisionWrite request, string? ownerId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Whether original relationship assertions and decision-owned endpoints can commit atomically.</summary>
    bool SupportsRelationshipIngestion => false;

    /// <summary>
    /// Store an immutable original relationship claim with optional source/target decision support.
    /// Unresolved claims remain inspectable without guessed links. Replay preserves original support.
    /// No ordinary RELATED_TO edge is adopted or rewritten by this operation.
    /// </summary>
    Task<IdentityRelationshipObservation> SaveRelationshipObservationAsync(IdentityRelationshipObservation observation,
        string? ownerId = null, bool includeShared = false, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This identity backend cannot retain relationship observations and endpoint support.");

    /// <summary>Inspect an original relationship claim in its exact owner scope; null means shared-only.</summary>
    Task<IdentityRelationshipObservation?> GetRelationshipObservationAsync(string observationId,
        string? ownerId = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This identity backend cannot read original relationship observations.");

    /// <summary>
    /// Apply an explicit scoped review atomically with its immutable revision and decision-owned support.
    /// The expected version prevents lost updates; an identical operation key is idempotent even after later reviews.
    /// No model runs inside this operation and no unowned ABOUT/RELATED_TO link is removed.
    /// </summary>
    Task<IdentityReviewResult> ReviewAsync(IdentityReviewRequest request, string? ownerId = null,
        bool includeShared = false, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This identity backend cannot review or revoke decisions atomically.");

    /// <summary>Newest-first immutable revisions in the exact owner scope, at most 64 per call.</summary>
    Task<IReadOnlyList<IdentityDecision>> GetHistoryAsync(string decisionId, string? ownerId = null,
        int limit = 32, long? beforeVersion = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This identity backend cannot read revision history.");

    /// <summary>
    /// The review queue: the owner's decisions that still hold open proposals (Pending candidates), in decision-id order
    /// for stable paging (pass the last returned id as <paramref name="afterDecisionId"/>). Bounded at 1 to 256 per call;
    /// a null owner lists shared decisions only. A reviewed proposal leaves the queue. Backends without a queue reject it.
    /// </summary>
    Task<IReadOnlyList<IdentityDecision>> ListPendingAsync(string? ownerId, int limit, string? afterDecisionId = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This backend does not list identity proposals for review.");

    /// <summary>Read the current decision only in its exact owner scope.</summary>
    Task<IdentityDecision?> GetAsync(string decisionId, string? ownerId = null,
        CancellationToken cancellationToken = default);
}
