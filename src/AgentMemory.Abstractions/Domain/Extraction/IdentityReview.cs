namespace AgentMemory.Abstractions.Domain;

/// <summary>Explicit host-authorized identity review, separate from model candidate similarity.</summary>
public enum IdentityReviewAction
{
    /// <summary>Confirm a scoped existing target after fresh domain checks.</summary>
    Confirm,
    /// <summary>Reject a currently pending candidate for this stable observation.</summary>
    Reject,
    /// <summary>Withdraw a confirmation and return support to the original observation.</summary>
    Revoke
}

/// <summary>One version-checked, idempotent review request. The host authorizes the actor.</summary>
public sealed record IdentityReviewRequest
{
    /// <summary>Decision in the exact owner scope.</summary>
    public required string DecisionId { get; init; }
    /// <summary>Stable operation key; retry the identical request to discover its committed outcome.</summary>
    public required string OperationId { get; init; }
    /// <summary>Current version the reviewer actually inspected.</summary>
    public required long ExpectedVersion { get; init; }
    /// <summary>Review action; deterministic incompatibilities still veto confirmation.</summary>
    public required IdentityReviewAction Action { get; init; }
    /// <summary>Existing candidate for confirm/reject; absent for revoke.</summary>
    public string? CandidateEntityId { get; init; }
    /// <summary>Host-authorized human/service identity, recorded for audit.</summary>
    public required string Actor { get; init; }
    /// <summary>Explicit review reason, retained independently of original observations.</summary>
    public required string Reason { get; init; }
}

/// <summary>Backend-generated immutable receipt for the operation that created a revision.</summary>
public sealed record IdentityReviewEntry
{
    /// <summary>Operation key supplied by the reviewer.</summary>
    public required string OperationId { get; init; }
    /// <summary>Backend-computed owner/request fingerprint; changing a retried request is rejected.</summary>
    public required string RequestHash { get; init; }
    /// <summary>The explicit review action.</summary>
    public required IdentityReviewAction Action { get; init; }
    /// <summary>The candidate considered, absent for revoke.</summary>
    public string? CandidateEntityId { get; init; }
    /// <summary>Host-authorized actor.</summary>
    public required string Actor { get; init; }
    /// <summary>Recorded review reason.</summary>
    public required string Reason { get; init; }
    /// <summary>Version the operation replaced.</summary>
    public required long PreviousVersion { get; init; }
    /// <summary>Committed revision created by this operation.</summary>
    public required long Version { get; init; }
    /// <summary>Backend clock time of review.</summary>
    public required DateTimeOffset PerformedAtUtc { get; init; }
}

/// <summary>Current state and the immutable operation outcome, including a later idempotent retry.</summary>
public sealed record IdentityReviewResult
{
    /// <summary>Current decision; it may include later reviews than the retried operation.</summary>
    public required IdentityDecision CurrentDecision { get; init; }
    /// <summary>The exact revision committed by this operation.</summary>
    public required IdentityDecision AppliedRevision { get; init; }
    /// <summary>True when the same operation had already committed, without another revision or rewire.</summary>
    public required bool AlreadyApplied { get; init; }
}
