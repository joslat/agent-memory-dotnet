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
