namespace AgentMemory.Abstractions.Options;

/// <summary>
/// The working-memory tier: a compiled per-owner profile block, on by default.
/// </summary>
/// <remarks>
/// <para>
/// <b>A mutable class, not an init-only record</b>, and that is the issue-#100 lesson rather than a
/// style choice: sub-options reached through a <c>configureMemory</c> lambda must be assignable, or
/// the option binds, validates, and silently keeps its default — code that compiles, runs, and
/// configures nothing.
/// </para>
/// <para>
/// <b>Cost is priced, not hidden.</b> The structured baseline is 403 tokens per question; a
/// 300-token block roughly doubles it, and is still about 1/400th of full-history. That is a
/// deliberate, declared context increase, which is why <see cref="MaxTokens"/> is a hard budget
/// rather than a hint.
/// </para>
/// </remarks>
public sealed class WorkingMemoryOptions
{
    /// <summary>
    /// On by default (since 2026-09-26). When false the block is never compiled, never stored, never
    /// rendered. Measured: a new session asked "what do you know about me?" answered with the user's name,
    /// job, employer, manager, family, neighbour and preferences with the block (3 of 3 runs) and "a pretty
    /// thin file" without it (2 of 2), at the same answer time: a generic question's embedding matches few
    /// stored facts, so similarity recall alone cannot answer it.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Hard token budget for the rendered block. Estimated as ceil(chars / 4).</summary>
    public int MaxTokens { get; set; } = 300;

    /// <summary>Most stable facts to include.</summary>
    public int MaxStableFacts { get; set; } = 12;

    /// <summary>Most active preferences to include.</summary>
    public int MaxActivePreferences { get; set; } = 8;

    /// <summary>Most salient entities to include.</summary>
    public int MaxTopEntities { get; set; } = 6;

    /// <summary>
    /// How often a fact must have been mentioned to earn a slot. 1 by default: most facts about a user are
    /// said once, and at 2 the block of a short relationship held no facts at all.
    /// </summary>
    public int MinFactMentionCount { get; set; } = 1;

    /// <summary>Confidence floor for a preference to earn a slot.</summary>
    public double MinPreferenceConfidence { get; set; } = 0.5;

    /// <summary>Rebuild the block after every long-term write. On by default <i>when the tier is enabled</i>.</summary>
    /// <remarks>
    /// Eager and full, with no partial invalidation — deliberately. Invalidation over a graph ("which
    /// writes touch which block inputs?") is the clever answer that goes stale; a stale block asserting
    /// a superseded value would <i>manufacture</i> failures in knowledge-update, the weakest measured
    /// non-episodic type. Correct-but-eager beats clever-but-stale.
    /// </remarks>
    public bool RebuildOnWrite { get; set; } = true;

    /// <summary>On a rebuild failure, clear the stored block rather than leaving it stale.</summary>
    /// <remarks>
    /// Absence degrades to today's behaviour; staleness manufactures errors. That asymmetry is why
    /// this defaults to true.
    /// </remarks>
    public bool ClearOnRebuildFailure { get; set; } = true;
}
