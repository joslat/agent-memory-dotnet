using System.Globalization;

namespace AgentMemory.LongMemEval;

/// <summary>
/// The full identity of a TypedMemEval run's arm: which levers were on when it was measured.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this closes.</b> Two runs differing only by a flag produced artifacts that were
/// indistinguishable on disk — same vertical, same seed, same shape — so identifying which was the
/// control required reading a shell log that no longer existed by the time anyone asked. An artifact
/// that cannot name its own arm is not evidence; it is a number with a story attached separately.
/// </para>
/// <para>
/// <b>Why this is a separate type from <see cref="PhaseThirtyFeatures"/>.</b> That record means
/// "engine features", and it earns its keep by deriving the schema extensions those features need
/// (<c>working-memory</c>, <c>arithmetic</c>). The rescue and budget levers are harness-side
/// retrieval settings with no DDL at all. Folding them in would give that type members its
/// <c>Extensions</c> property has to deliberately ignore, which is how a type stops meaning one
/// thing. They compose here instead.
/// </para>
/// <para>
/// <b><see cref="PhaseThirtyFeatures.Describe"/> was dead code.</b> Its own docstring said "run
/// provenance and report file names" and nothing called it — the sixteenth ship-but-unreachable
/// instance found in this repository, in the very type built to make arms legible. It is now
/// reachable from the filename path, which is what it was written for.
/// </para>
/// </remarks>
/// <param name="Phase30">Engine features under test.</param>
/// <param name="RescueShortOwnerResults">Whether the short-owner-result rescue was enabled.</param>
/// <param name="SupersedeReplacedFacts">
/// Whether write-time supersession was enabled. It defaults OFF in the engine, and the four-vertical
/// run measured Bitemporal without it -- an append-only store with no <c>invalidated_at</c> and no
/// <c>:SUPERSEDED_BY</c> edge. The arm token must name it, or an ON artifact and an OFF artifact are
/// once again indistinguishable from each other.
/// </param>
/// <param name="FactWeightedBudget">Whether the recall budget was reallocated toward facts.</param>
/// <param name="ExpandFactsByPredicate">
/// Whether a matched relation was returned WHOLE rather than by similarity rank. The lever the
/// C-D family run proved was missing: its own comment in the adapter says it exists "for the
/// aggregation questions top-K structurally cannot answer", and it was set only by the LongMemEval
/// verb -- so every TypedMemEval number was taken with the aggregation machinery hard off.
/// </param>
/// <param name="ResolveQueryRelations">
/// Whether relations the question itself names were expanded. Pairs with
/// <paramref name="ExpandFactsByPredicate"/>: that one completes a relation once found, this one
/// finds the relations a multi-relation question nominates.
/// </param>
/// <param name="MaxDerivedFacts">
/// How derived facts were budgeted. <c>null</c> is the pre-existing behaviour AND the measured-harmful
/// one: the accountant's counts and sums compete for the ordinary fact budget and displace the source
/// values they were computed from (arithmetic 30% to 14%). Named in the token because a run with the
/// read side on and one without it are otherwise indistinguishable on disk.
/// </param>
/// <param name="RecallFanOut">
/// Whether per-memory-type recall fan-out ran. <c>RecallFanOutOptions.Enabled</c> defaults false and
/// NOTHING under <c>tools/</c> set it, so the feature built for multi-hop recall had never been
/// switched on in any measurement this project holds.
/// </param>
/// <param name="ResolveSupersessions">
/// Whether the supersession CHAIN was rendered into the prompt. Deliberately APPENDED rather than
/// grouped beside <paramref name="SupersedeReplacedFacts"/>, which is where it belongs by
/// meaning: inserting a positional parameter mid-record silently re-slots every positional call
/// site, and that exact mistake turned a `factwt` arm into a `supersede` arm once already. Meaning
/// loses to safety here, and the pairing is documented instead. The three retrieval levers after it
/// were appended under the same rule, which is why it is no longer the last parameter.
/// <para>
/// The two levers are only informative TOGETHER. <see cref="SupersedeReplacedFacts"/> writes the
/// <c>:SUPERSEDED_BY</c> edges; this renders them. Rendering alone has nothing to read, and writing
/// alone filters the superseded value out of recall while giving the model no cue that the surviving
/// value ever replaced anything -- which is the off-state the 0.767 ON ablation actually measured.
/// </para>
/// </param>
/// <param name="CurrentValidTimeOnly">
/// Sets <c>RecallOptions.ValidTime</c> to <c>Current</c>, so only facts whose valid-time window
/// contains the present are returned. Appended last under the positional-safety rule documented on
/// <paramref name="ResolveSupersessions"/>.
/// </param>
/// <param name="ProspectiveFiring">
/// Volunteers facts that just became due, or are about to expire — selected by TIME, never by
/// similarity.
/// <para>
/// <b>These two levers are only informative TOGETHER, and that is a property of the engine, not a
/// convention.</b> <c>MemoryContextAssembler</c> gates firing on
/// <c>ProspectiveFiring &amp;&amp; ValidTime == ValidTimeMode.Current</c>: firing reads a fact's
/// valid-time window, and a recall ignoring valid time has no window to read. So enabling firing
/// alone changes NOTHING, and enabling both changes two things at once.
/// </para>
/// <para>
/// <b>Which is why they are two flags and two tokens rather than one.</b> An ablation that moved
/// both could not attribute a difference to firing rather than to the valid-time filter. The third
/// arm — valid-time on, firing off — is what separates them, and it is expressible only because the
/// levers stayed separate here.
/// </para>
/// <para>
/// <b>Never set by any harness before now.</b> <c>RecallOptions.ProspectiveFiring</c> is public,
/// consumed by the assembler and covered by three unit-test classes, and the benchmark harness's
/// single <c>RecallOptions</c> construction never assigned it — nor <c>ValidTime</c>, which defaults
/// to <c>Ignore</c>. Every prospective number this project has produced, <c>due-window</c> 1/18
/// included, was measured with firing dark on BOTH conditions.
/// </para>
/// </param>
public sealed record TypedMemEvalArm(
    PhaseThirtyFeatures Phase30,
    bool RescueShortOwnerResults = false,
    bool SupersedeReplacedFacts = false,
    bool FactWeightedBudget = false,
    bool ResolveSupersessions = false,
    bool ExpandFactsByPredicate = false,
    bool ResolveQueryRelations = false,
    bool RecallFanOut = false,
    int? MaxDerivedFacts = null,
    bool CurrentValidTimeOnly = false,
    bool ProspectiveFiring = false,
    bool LinkFactsToEntities = false,
    bool NodeDistanceReranking = false,
    bool TemporalValidity = false,
    bool CaptureIdentityAliases = false,
    bool ExpandFactsByIdentity = false)
{
    /// <summary>The shipped default: every lever off, which is how the sealed measurements were taken.</summary>
    public static TypedMemEvalArm Default { get; } = new(PhaseThirtyFeatures.AllOff);

    /// <summary>True when nothing is enabled, so a run can assert it took the default path.</summary>
    public bool IsDefault =>
        Phase30.IsDefault && !RescueShortOwnerResults && !FactWeightedBudget
        && !SupersedeReplacedFacts && !ResolveSupersessions
        && !ExpandFactsByPredicate && !ResolveQueryRelations && !RecallFanOut
        && MaxDerivedFacts is null && !CurrentValidTimeOnly && !ProspectiveFiring
        && !LinkFactsToEntities && !NodeDistanceReranking && !TemporalValidity
        && !CaptureIdentityAliases && !ExpandFactsByIdentity;

    /// <summary>
    /// A filename-safe token naming every enabled lever, or <c>"default"</c> when none is.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="PhaseThirtyFeatures.Describe"/>'s <c>"phase30:none"</c> string: a
    /// colon is not legal in a Windows filename, and a token that has to be sanitised at each use is
    /// a token that will eventually be sanitised differently at one of them.
    /// </remarks>
    public string FileToken()
    {
        if (IsDefault) return "default";

        var parts = new List<string>(4);
        if (Phase30.WorkingMemory) parts.Add("wm");
        if (Phase30.ArithmeticMemory) parts.Add("arith");
        if (RescueShortOwnerResults) parts.Add("rescue");
        if (SupersedeReplacedFacts) parts.Add("supersede");
        if (ResolveSupersessions) parts.Add("render");
        if (FactWeightedBudget) parts.Add("factwt");
        if (ExpandFactsByPredicate) parts.Add("expand");
        if (ResolveQueryRelations) parts.Add("qrel");
        if (RecallFanOut) parts.Add("fanout");
        // The VALUE is in the token, not just the fact that it was set: 0 (exclude) and 10 (own
        // budget) are different arms with different predictions, and an artifact that could not tell
        // them apart would be a number with its story attached separately -- the defect this whole
        // type exists to close.
        if (MaxDerivedFacts is { } derived) parts.Add($"derived{derived.ToString(CultureInfo.InvariantCulture)}");
        // Two tokens, never one. Firing CANNOT fire without current-valid-time, so a single "firing"
        // token would name an arm that also changed how facts are filtered -- and an artifact that
        // cannot distinguish "firing" from "firing plus a valid-time filter" cannot attribute a
        // difference to either.
        if (CurrentValidTimeOnly) parts.Add("vtcurrent");
        if (ProspectiveFiring) parts.Add("firing");
        // An INGESTION lever, unlike every other token here: it changes the store the questions are
        // asked against, not how that store is read. Two arms differing by it are not two readings of
        // one corpus -- they are two corpora -- which is exactly why it must be nameable on disk.
        if (LinkFactsToEntities) parts.Add("entlink");
        // The READ side of the identity edge. Only informative with `entlink`: the re-ranker's path
        // walks [:RELATED_TO|ABOUT*..4], and a store with no ABOUT edge gives it nothing to follow.
        if (NodeDistanceReranking) parts.Add("noderank");
        // An INGESTION lever, and the precondition for firing: without validity windows no fact can
        // ever be "due", so `vtcurrent` filters nothing and `firing` fires nothing.
        if (TemporalValidity) parts.Add("tvalid");
        // E-1, and an INGESTION lever: it changes what extraction records, so two arms differing by
        // it are two stores and may never be banded together.
        if (CaptureIdentityAliases) parts.Add("alias");
        // The READ side of the alias. It traverses :ABOUT, so it is only informative alongside
        // `entlink` -- with no ABOUT edge there is nothing to hop -- and only useful alongside
        // `alias`, since the hop is restricted to entities that carry one. The full intervention is
        // `entlink-alias-aliashop`; any shorter arm measures a part that cannot work alone, which is
        // the mistake the first three identity arms made one at a time.
        if (ExpandFactsByIdentity) parts.Add("aliashop");
        return string.Join("-", parts);
    }

    /// <summary>The human-readable arm, for logs and the provenance sidecar.</summary>
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Phase30.Describe()} rescue-short-owner-results={RescueShortOwnerResults} " +
        $"supersede-replaced-facts={SupersedeReplacedFacts} " +
        $"resolve-supersessions={ResolveSupersessions} " +
        $"fact-weighted-budget={FactWeightedBudget} " +
        $"expand-facts-by-predicate={ExpandFactsByPredicate} " +
        $"resolve-query-relations={ResolveQueryRelations} " +
        $"recall-fan-out={RecallFanOut} " +
        $"max-derived-facts={(MaxDerivedFacts is { } d ? d.ToString(CultureInfo.InvariantCulture) : "null")} " +
        $"current-valid-time-only={CurrentValidTimeOnly} " +
        $"prospective-firing={ProspectiveFiring} " +
        $"link-facts-to-entities={LinkFactsToEntities} " +
        $"node-distance-reranking={NodeDistanceReranking} " +
        $"temporal-validity={TemporalValidity} " +
        $"capture-identity-aliases={CaptureIdentityAliases} " +
        $"expand-facts-by-identity={ExpandFactsByIdentity}");
}
