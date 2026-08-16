namespace AgentMemory.Abstractions.Options;

/// <summary>
/// Which projection features render what the store already knows but every renderer discarded.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every flag is off by default, and off means byte-identical.</b> With all of them off the
/// assembler attaches no <c>ProjectedContext</c> at all and each render surface takes its exact
/// pre-existing code path — asserted by a SHA256 fingerprint over the rendered output that was
/// captured before any of this code existed.
/// </para>
/// <para>
/// <b>Why these five and not richer renderers.</b> Retrieval computes a similarity score for every
/// item and every renderer throws it away, so a 0.72 near-miss reads identically to a 0.99 match;
/// the graph holds <c>SUPERSEDED_BY</c> edges, conflict findings and real dates that never reach a
/// prompt; and triples drop the tense, participants and ordinals their source sentences carry. Each
/// is a measured loss with a named failing question behind it, and each is a separate flag because
/// each has to earn its default independently.
/// </para>
/// </remarks>
public sealed record MemoryProjectionOptions
{
    /// <summary>The all-off default. Reference-compared, so "unset" is distinguishable from "set to the defaults".</summary>
    public static MemoryProjectionOptions Default { get; } = new();

    /// <summary>
    /// Renders how well each recalled item actually matched, and says so when nothing matched well.
    /// </summary>
    /// <remarks>
    /// The one memory-layer-fixable abstention failure: a question whose best evidence sat at 0.857
    /// coverage produced a confidently confabulated answer, because the prompt gave the model no way
    /// to tell a near-miss from a hit.
    /// </remarks>
    public bool AnnotateMatchQuality { get; init; }

    /// <summary>Scores below this render as a closest-match rather than a match. A prior, not a measurement.</summary>
    public double NearMissThreshold { get; init; } = 0.85;

    /// <summary>
    /// The near-miss threshold for reasoning traces, defaulted to the <b>measured</b> knee.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="NearMissThreshold"/> because procedure retrieval was measured to
    /// behave identically for every threshold from 0.00 to 0.86 — a dead zone in which it never
    /// abstains. 0.92 is the knee; the shared 0.85 prior would sit inside the dead zone and do
    /// nothing.
    /// </remarks>
    public double TraceNearMissThreshold { get; init; } = 0.92;

    /// <summary>Renders "current X (since D; previously Y)" using supersession edges live recall filters out.</summary>
    public bool ResolveSupersessions { get; init; }

    /// <summary>How many superseded predecessors to render before stopping.</summary>
    public int MaxSupersessionChain { get; init; } = 3;

    /// <summary>Renders two live recalled facts that contradict each other as an explicit conflict.</summary>
    public bool RenderConflicts { get; init; }

    /// <summary>Attaches the shortest source sentence containing a fact's object, restoring tense and participants.</summary>
    public bool AttachSourceQuotes { get; init; }

    /// <summary>Longest quote rendered before truncation; the prize is accuracy at ~500 tokens, not 2,505.</summary>
    public int MaxQuoteLength { get; init; } = 160;

    /// <summary>Cap on quotes per recall, so the token cost of this feature is bounded by construction.</summary>
    public int MaxQuotesPerRecall { get; init; } = 10;

    /// <summary>Prefixes date-bearing items with their real source date.</summary>
    public bool GroundDates { get; init; }

    /// <summary>Orders date-bearing items chronologically <b>within</b> a section. No cross-section interleaving, no computed intervals.</summary>
    public bool ChronologicalOrdering { get; init; }
}
