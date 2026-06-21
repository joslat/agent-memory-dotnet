namespace AgentMemory.Abstractions.Options;

/// <summary>
/// Configuration for memory decay and forgetting behavior.
/// Controls how unused memories lose value over time and when they become eligible for pruning.
/// </summary>
public sealed record MemoryDecayOptions
{
    /// <summary>
    /// Half-life in days for the exponential decay function.
    /// After this many days without access, a memory's recency component drops to 50%.
    /// </summary>
    public double DecayHalfLifeDays { get; init; } = 30;

    /// <summary>
    /// Minimum retention score (0.0–1.0) below which memories are eligible for pruning.
    /// </summary>
    public double MinRetentionScore { get; init; } = 0.1;

    // NOTE: long-term memory (Entity/Fact/Preference) is cross-session knowledge — those nodes carry an
    // owner_id, not a session_id — so a "max nodes per session" cap is not meaningful here; pruning is
    // owner-scoped and retention-score driven (see MinRetentionScore / DecayHalfLifeDays / AccessBoostFactor).
    // The genuinely session-scoped cap lives on ReasoningMemoryOptions.MaxTracesPerSession. The former
    // MaxMemoriesPerSession property here was read nowhere and could not be coherently enforced, so it was removed.

    /// <summary>
    /// Boost factor applied per access (recall hit) when computing the retention score.
    /// </summary>
    public double AccessBoostFactor { get; init; } = 0.2;

    // NOTE: a documented-but-dead `EnableAutoPrune` option was removed (R6 cleanup). It promised
    // "automatically prune during extraction" but was read nowhere — auto-prune-on-extraction would wire
    // the decay service into the extraction pipeline, which belongs with the (currently held) decay/forget
    // work, not a settable flag that silently does nothing. Pruning runs only when explicitly invoked.

    /// <summary>
    /// When <c>true</c> (the default, D4), decay pruning is <b>non-destructive</b>: low-score nodes are
    /// soft-invalidated (their <c>invalidated_at</c> is stamped) — kept, recoverable, and still visible to
    /// as-of recall — rather than deleted, so forgetting is reversible and auditable. Set <c>false</c> for
    /// a hard <c>DETACH DELETE</c> purge (storage reclamation / GDPR erasure). Pruning only runs when
    /// explicitly invoked; it is never automatic.
    /// </summary>
    public bool NonDestructive { get; init; } = true;

    /// <summary>Default instance with standard values.</summary>
    public static MemoryDecayOptions Default { get; } = new();
}
