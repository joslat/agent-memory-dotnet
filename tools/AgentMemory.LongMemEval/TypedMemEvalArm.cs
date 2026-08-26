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
public sealed record TypedMemEvalArm(
    PhaseThirtyFeatures Phase30,
    bool RescueShortOwnerResults = false,
    bool SupersedeReplacedFacts = false,
    bool FactWeightedBudget = false)
{
    /// <summary>The shipped default: every lever off, which is how the sealed measurements were taken.</summary>
    public static TypedMemEvalArm Default { get; } = new(PhaseThirtyFeatures.AllOff);

    /// <summary>True when nothing is enabled, so a run can assert it took the default path.</summary>
    public bool IsDefault =>
        Phase30.IsDefault && !RescueShortOwnerResults && !FactWeightedBudget
        && !SupersedeReplacedFacts;

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
        if (FactWeightedBudget) parts.Add("factwt");
        return string.Join("-", parts);
    }

    /// <summary>The human-readable arm, for logs and the provenance sidecar.</summary>
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Phase30.Describe()} rescue-short-owner-results={RescueShortOwnerResults} " +
        $"supersede-replaced-facts={SupersedeReplacedFacts} " +
        $"fact-weighted-budget={FactWeightedBudget}");
}
