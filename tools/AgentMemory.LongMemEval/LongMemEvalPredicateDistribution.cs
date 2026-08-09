using System.Text;
using AgentMemory.Core.Memory;

namespace AgentMemory.LongMemEval;

/// <summary>One canonical predicate as it actually occurs in an extracted graph.</summary>
/// <remarks>
/// Deliberately carries no subject or object. A predicate is vocabulary; a subject or object is user
/// content, and this table is written to an artifact.
/// </remarks>
internal sealed record LongMemEvalPredicateCount(string Predicate, int FactCount, int OwnerCount);

internal sealed record LongMemEvalPredicateDistributionSummary(
    int RawPredicateCount,
    int CanonicalPredicateCount,
    int TotalFactCount,
    int OwnerCount,
    IReadOnlyList<LongMemEvalPredicateCount> Predicates)
{
    /// <summary>
    /// How many surface predicates collapsed onto each canonical one. This measures the
    /// <b>canonicalizer</b>, which folds case and separators only, and is expected to sit near 1.00.
    /// It is <b>not</b> the vocabulary consolidation figure, which is measured per owner.
    /// </summary>
    internal double ConsolidationRatio => CanonicalPredicateCount == 0
        ? 0
        : (double)RawPredicateCount / CanonicalPredicateCount;

    internal int MinPredicatesPerOwner { get; init; }

    internal int MaxPredicatesPerOwner { get; init; }

    internal double AveragePredicatesPerOwner { get; init; }
}

internal sealed record LongMemEvalPredicateSplit(
    IReadOnlyList<LongMemEvalPredicateCount> Build,
    IReadOnlyList<LongMemEvalPredicateCount> HeldOut)
{
    internal int BuildFactCount => Build.Sum(item => item.FactCount);

    internal int HeldOutFactCount => HeldOut.Sum(item => item.FactCount);
}

/// <summary>
/// J1.2. Produces the observed predicate distribution and its build / held-out split.
/// </summary>
/// <remarks>
/// The split is <b>by predicate, not by fact</b>: the question the held-out slice answers is "does the
/// vocabulary cover relations it was not built against", which requires holding out whole relations.
/// It is deterministic for a seed so that a coverage number is reproducible - an irreproducible gate
/// is the same defect as an irreproducible score.
/// </remarks>
internal static class LongMemEvalPredicateDistribution
{
    internal static LongMemEvalPredicateSplit Split(
        IReadOnlyList<LongMemEvalPredicateCount> predicates,
        double heldOutFraction,
        int seed)
    {
        ArgumentNullException.ThrowIfNull(predicates);
        if (heldOutFraction is <= 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(heldOutFraction), "The held-out fraction must be between 0 and 1 exclusive.");
        }

        // Rank by a seeded stable hash and take a prefix, rather than testing each predicate against a
        // probability. A per-item probability makes the slice size vary with the seed, and an empty
        // held-out slice would silently turn the generalisation gate into a no-op.
        var ordered = predicates
            .OrderBy(item => StableHash(item.Predicate, seed))
            .ThenBy(item => item.Predicate, StringComparer.Ordinal)
            .ToArray();

        var heldOutCount = Math.Clamp(
            (int)Math.Round(ordered.Length * heldOutFraction, MidpointRounding.AwayFromZero),
            1,
            Math.Max(1, ordered.Length - 1));

        return new LongMemEvalPredicateSplit(
            ordered.Skip(heldOutCount).ToArray(),
            ordered.Take(heldOutCount).ToArray());
    }

    /// <summary>
    /// FNV-1a over the seed and the predicate. Hand-rolled because <see cref="string.GetHashCode()"/>
    /// is randomized per process, which would make the split differ between runs of the same command.
    /// </summary>
    private static uint StableHash(string value, int seed)
    {
        unchecked
        {
            var hash = 2166136261u ^ (uint)seed;
            foreach (var b in Encoding.UTF8.GetBytes(value))
            {
                hash ^= b;
                hash *= 16777619u;
            }

            return hash;
        }
    }

    /// <summary>
    /// J1.5 gate 1. Coverage of a predicate slice by the shipped relation lexicon, split into bands
    /// by how many facts each predicate carries.
    /// </summary>
    /// <remarks>
    /// The unbanded statistic failed at 15.4 points and was root-caused to skew, not to a real
    /// generalisation gap: coverage over a long tail of one-fact predicates is dominated by the tail,
    /// so a slice that happens to draw more singletons looks worse regardless of the vocabulary. The
    /// banded form asks the question that actually matters — <b>are the predicates carrying real
    /// weight covered?</b> — and it is checked per band in both slices rather than averaged into one
    /// number that hides exactly the skew that broke the first version.
    /// <para>
    /// Membership goes through the shipped <see cref="MemoryRelationLexicon"/>, not a reimplementation
    /// of it. A gate scored against a replica of the thing under test measures the replica.
    /// </para>
    /// <para>
    /// It asks <see cref="MemoryRelationLexicon.IsKnownStoredForm"/>, not <c>Resolve</c>. Resolve is
    /// the query-side method and rejects stop forms by design, so scoring storage coverage with it
    /// counts <c>has</c> and <c>is</c> — 2,701 facts between them — as unknown vocabulary. The first
    /// run of this gate made exactly that mistake and read 81.5%.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<PredicateCoverageBand> CoverageBands(
        IReadOnlyList<LongMemEvalPredicateCount> predicates)
    {
        ArgumentNullException.ThrowIfNull(predicates);

        // Chosen to separate "carries the graph" from "appeared once": the >=10 band is the one the
        // gate binds on, and the singleton band is reported rather than dropped so a regression that
        // hides in the tail is still visible.
        (string Label, int Lower, int Upper)[] bands =
        [
            ("10+", 10, int.MaxValue),
            ("3-9", 3, 9),
            ("2", 2, 2),
            ("1", 1, 1)
        ];

        return bands.Select(band =>
        {
            var members = predicates
                .Where(entry => entry.FactCount >= band.Lower && entry.FactCount <= band.Upper)
                .ToArray();
            var resolved = members
                .Where(entry => MemoryRelationLexicon.Default.IsKnownStoredForm(entry.Predicate))
                .ToArray();
            return new PredicateCoverageBand(
                band.Label,
                members.Length,
                resolved.Length,
                members.Length == 0 ? 1d : (double)resolved.Length / members.Length,
                members
                    .Where(entry => !MemoryRelationLexicon.Default.IsKnownStoredForm(entry.Predicate))
                    .OrderByDescending(entry => entry.FactCount)
                    .ThenBy(entry => entry.Predicate, StringComparer.Ordinal)
                    .Select(entry => entry.Predicate)
                    .ToArray());
        }).ToArray();
    }

}

/// <summary>One fact-count band of a predicate slice and how much of it the lexicon resolves.</summary>
internal sealed record PredicateCoverageBand(
    string Band,
    int PredicateCount,
    int ResolvedCount,
    double Coverage,
    IReadOnlyList<string> Unresolved);
