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

    /// <summary>
    /// Of <see cref="MaxStableFacts"/>, how many slots go to the most recently learned facts that did not
    /// win a slot by mentions (default 4). Without them, once every slot holds a fact mentioned twice or
    /// more, a new job or city (mentioned once) never reaches the block. 0 = slots by mentions only, the
    /// behaviour before this existed.
    /// </summary>
    public int RecentStableFactSlots { get; set; } = 4;

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

    /// <summary>
    /// 36.1. The block's facts carry their validity dates, at the precision they were stated
    /// (<c>Rosa works at the hospital (since 2021)</c>), by the same rule the recall renderers use. Default true (37.1),
    /// as for the recall renderers; false builds the block as before, without dates.
    /// </summary>
    public bool IncludeDates { get; set; } = true;

    /// <summary>
    /// 37.5. "What's been on the person's mind lately": the block ends with one line naming the topics the person
    /// talked about most in the last this-many days (<c>Lately (7 days): marathon (5 mentions), Ana (2 mentions)</c>).
    /// 0, the default, leaves the block as it was.
    /// </summary>
    /// <remarks>
    /// A topic is an entity of the owner that a live fact names as its subject or object, never the person
    /// themselves; it is counted by the distinct facts naming it that were extracted, in the window, from what the
    /// person said (the facts' <c>EXTRACTED_FROM</c> provenance to live user messages with their time). What the agent
    /// said or recalled does not count. No decay inside the window; ties break by name, so the line is stable between
    /// rebuilds with the same inputs; the block is rebuilt at least daily while it carries the line, so it slides.
    /// </remarks>
    public int RecentTopicsDays { get; set; }

    /// <summary>37.5. Most topics on the "Lately" line (default 3).</summary>
    public int MaxRecentTopics { get; set; } = 3;

    /// <summary>37.5. How many mentions in the window make a topic "top of mind" (default 2: said once is not lately).</summary>
    public int MinRecentTopicMentions { get; set; } = 2;

    /// <summary>On a rebuild failure, clear the stored block rather than leaving it stale.</summary>
    /// <remarks>
    /// Absence degrades to today's behaviour; staleness manufactures errors. That asymmetry is why
    /// this defaults to true.
    /// </remarks>
    public bool ClearOnRebuildFailure { get; set; } = true;
}
