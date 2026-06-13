namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Detects contradictions in long-term memory (detect-only; never mutates). v1 finds <b>fact
/// contradictions</b>: facts sharing the same subject + predicate within an owner scope but asserting
/// two or more distinct objects (e.g. "Alice / works_at / Acme" vs "Alice / works_at / Globex").
/// Conflicts are grouped per owner (a user's facts conflict among themselves; shared/global facts
/// among themselves), so detection respects R1 isolation. Pairs with the consolidation service to form
/// the memory-hygiene story.
/// </summary>
public interface IConflictDetectionService
{
    /// <summary>
    /// Scans for conflicts and returns a report. Nothing is mutated — resolution is left to the caller.
    /// </summary>
    Task<ConflictReport> DetectConflictsAsync(
        ConflictDetectionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opt-in resolution (D7): detects fact contradictions and <b>closes the losers non-destructively</b>
    /// — for each group it keeps a winner and supersedes the rest (stamping <c>invalidated_at</c>/
    /// <c>valid_until</c> and linking <c>:SUPERSEDED_BY</c> to the winner), so the losers drop from live
    /// recall but are kept and stay visible to as-of recall before resolution. Detection
    /// (<see cref="DetectConflictsAsync"/>) remains the non-mutating default; callers must explicitly opt
    /// into this. Resolution respects R1 owner isolation (each group is resolved within its own owner
    /// scope). The winner-selection policy is the highest-confidence assertion (see
    /// <see cref="ConflictResolutionOptions"/>).
    /// </summary>
    Task<ConflictResolutionResult> ResolveFactContradictionsAsync(
        ConflictResolutionOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Toggles and thresholds for <see cref="IConflictDetectionService.DetectConflictsAsync"/>.</summary>
public sealed record ConflictDetectionOptions
{
    /// <summary>Detect fact contradictions (same subject + predicate + owner, ≥2 distinct objects). Default true.</summary>
    public bool DetectFactContradictions { get; init; } = true;

    /// <summary>Only consider facts at or above this confidence (null = no gate). Default null.</summary>
    public double? MinConfidence { get; init; }

    /// <summary>Maximum number of conflict groups to return (anti-runaway cap). Default 100.</summary>
    public int MaxConflicts { get; init; } = 100;
}

/// <summary>Result of a conflict-detection scan (detect-only).</summary>
public sealed record ConflictReport
{
    /// <summary>When the scan ran.</summary>
    public required DateTimeOffset RanAtUtc { get; init; }

    /// <summary>Detected fact contradictions.</summary>
    public IReadOnlyList<FactConflict> FactConflicts { get; init; } = Array.Empty<FactConflict>();

    /// <summary>Total number of fact-contradiction groups detected.</summary>
    public int FactConflictCount => FactConflicts.Count;
}

/// <summary>
/// A fact contradiction: one subject + predicate (within an owner scope) with multiple distinct objects.
/// </summary>
public sealed record FactConflict(
    string Subject,
    string Predicate,
    string? OwnerId,
    IReadOnlyList<ConflictingFactValue> Values);

/// <summary>One competing assertion within a <see cref="FactConflict"/>.</summary>
public sealed record ConflictingFactValue(string FactId, string Object, double Confidence);

/// <summary>
/// Toggles and policy for <see cref="IConflictDetectionService.ResolveFactContradictionsAsync"/>. The
/// winner of each contradiction group is the <b>highest-confidence</b> assertion (ties broken
/// deterministically by fact id); every other member is superseded by it. This is intentionally
/// conservative and reversible — losers are soft-invalidated, never deleted.
/// </summary>
public sealed record ConflictResolutionOptions
{
    /// <summary>
    /// Floor on the <b>winner's</b> confidence: a contradiction group is only resolved when its
    /// highest-confidence assertion is at or above this value (null = always resolve). This gates whole
    /// groups, not membership — it never removes a member from a group, so it cannot hide the genuine
    /// winner. Default null.
    /// </summary>
    public double? MinConfidence { get; init; }

    /// <summary>Maximum number of conflict groups to resolve in one pass (anti-runaway cap). Default 100.</summary>
    public int MaxConflicts { get; init; } = 100;
}

/// <summary>Result of an opt-in conflict-resolution pass (D7).</summary>
public sealed record ConflictResolutionResult
{
    /// <summary>When the resolution ran.</summary>
    public required DateTimeOffset RanAtUtc { get; init; }

    /// <summary>Number of contradiction groups that had a loser superseded.</summary>
    public int ConflictsResolved { get; init; }

    /// <summary>Total number of losing facts superseded across all groups.</summary>
    public int FactsSuperseded { get; init; }
}
