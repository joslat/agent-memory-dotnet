using AgentMemory.Abstractions.Options;

namespace AgentMemory.Abstractions.Domain;

/// <summary>The original position of a mention within a fact, independent of its later identity.</summary>
public enum IdentityFactRole
{
    /// <summary>The fact's original subject.</summary>
    Subject,
    /// <summary>The fact's original object.</summary>
    Object
}

/// <summary>
/// Durable interpretation of one source-grounded mention. Observation and context are retained;
/// a confirmed target is referenced, never rewritten as the observation. Review creates revisions.
/// </summary>
public sealed record IdentityDecision
{
    /// <summary>Stable decision identifier, reused on ingestion retries.</summary>
    public required string DecisionId { get; init; }
    /// <summary>Stable owner/root-source/mention fingerprint, independent of a policy version.</summary>
    public required string ObservationKey { get; init; }
    /// <summary>Original entity words and reserved separate ID, retained even after confirmation.</summary>
    public required Entity Observation { get; init; }
    /// <summary>Original source fragments, roots and host assertions, separate from model attributes.</summary>
    public EntityIdentityResolutionContext Context { get; init; } = new();
    /// <summary>Immutable policy snapshot used for this interpretation.</summary>
    public required EntityIdentityPolicy Policy { get; init; }
    /// <summary>Current interpretation, distinct from retrieval similarity.</summary>
    public required IdentityDecisionKind Status { get; init; }
    /// <summary>Existing target only for a confirmed interpretation; otherwise null.</summary>
    public string? ConfirmedEntityId { get; init; }
    /// <summary>Review candidates; these never create a legacy SAME_AS equivalence.</summary>
    public IReadOnlyList<string> PendingEntityIds { get; init; } = Array.Empty<string>();
    /// <summary>Structured, bounded evidence receipt. Opaque text cannot authorise a confirmation.</summary>
    public string EvidenceJson { get; init; } = "{}";
    /// <summary>Optional contextual interpretation. The backend rechecks source and target freshness before confirmation.</summary>
    public IdentityAdjudicationEvidence? ContextualEvidence { get; init; }
    /// <summary>Latest explicit review receipt; previous receipts remain in immutable revisions.</summary>
    public IdentityReviewEntry? LatestReview { get; init; }
    /// <summary>Current revision. A new decision starts at one.</summary>
    public long Version { get; init; } = 1;
    /// <summary>Reasons or conservative downgrades retained with this revision.</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
    /// <summary>Observation timestamp; never replaced by a later presentation-name change.</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }
    /// <summary>Time of the current decision revision.</summary>
    public required DateTimeOffset UpdatedAtUtc { get; init; }
}

/// <summary>Decision-owned support for one original fact role, using the final stored Fact ID.</summary>
public sealed record IdentityFactBinding
{
    /// <summary>The repository-returned stable fact ID, which may predate this ingestion.</summary>
    public required string FactId { get; init; }
    /// <summary>Subject or object position whose original words identify this observation.</summary>
    public required IdentityFactRole Role { get; init; }
}

/// <summary>
/// One atomic initial write or idempotent replay. The backend revalidates scoped candidate evidence;
/// a stale or contradictory plan cannot overwrite the original observation or silently merge it.
/// </summary>
public sealed record IdentityDecisionWrite
{
    /// <summary>Proposed initial interpretation. Existing decisions keep their reviewed revision.</summary>
    public required IdentityDecision Decision { get; init; }
    /// <summary>Bounded snapshots whose evidence versions and identity-relevant content are rechecked.</summary>
    public IReadOnlyList<IdentityCandidate> ExpectedCandidates { get; init; } = Array.Empty<IdentityCandidate>();
    /// <summary>Decision-owned fact/role bindings to commit in the same transaction.</summary>
    public IReadOnlyList<IdentityFactBinding> Bindings { get; init; } = Array.Empty<IdentityFactBinding>();
    /// <summary>Whether an owned decision may reference shared target entities.</summary>
    public bool IncludeShared { get; init; }
}
