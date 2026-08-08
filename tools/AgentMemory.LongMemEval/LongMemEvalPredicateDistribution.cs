using System.Text;

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
}
