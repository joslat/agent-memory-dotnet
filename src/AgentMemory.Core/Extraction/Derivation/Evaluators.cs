using System.Globalization;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Core.Extraction.Derivation;

/// <summary>How many live facts share this subject and predicate.</summary>
/// <remarks>
/// The operator the whole feature was named for: "how many fish did I catch in total" is a question
/// top-K retrieval structurally cannot answer, because the answer is a property of the <i>set</i> and
/// retrieval returns a sample of it.
/// </remarks>
internal sealed class CountEvaluator : IDerivationEvaluator
{
    public DerivationOperators Operator => DerivationOperators.Count;

    public DerivedCandidate? Evaluate(DerivationGroup group)
    {
        // Fan-in of one is not a count, it is the fact itself restated with extra ceremony.
        if (group.Facts.Count < 2) return null;

        var ids = group.Facts.Select(f => f.FactId).ToArray();
        return new DerivedCandidate(
            group.Subject,
            DerivedPredicates.For(DerivationOperators.Count, group.Predicate),
            group.Facts.Count.ToString(CultureInfo.InvariantCulture),
            $"{group.Facts.Count} live facts of {group.Predicate} ({string.Join(", ", ids)})",
            ids,
            DerivationOperators.Count);
    }
}

/// <summary>The change between the first and last numeric value in the chain.</summary>
/// <remarks>
/// The adjudicated case this design was built around: the store holds <c>800</c> and <c>50</c>, and the
/// answer is <c>750</c>. Direction is first-to-last, which is why group ordering is part of the
/// contract rather than an implementation detail.
/// </remarks>
internal sealed class DeltaEvaluator : IDerivationEvaluator
{
    public DerivationOperators Operator => DerivationOperators.Delta;

    public DerivedCandidate? Evaluate(DerivationGroup group)
    {
        if (group.Facts.Count < 2) return null;

        var numeric = NumericFacts(group);
        // ANY unparsable object disqualifies the group. Computing a delta across the parsable subset
        // would silently answer a different question than the one asked -- the change between two
        // values that happened to be readable is not the change over the chain.
        if (numeric is null) return null;

        var (firstFact, firstValue) = numeric[0];
        var (lastFact, lastValue) = numeric[^1];
        var delta = lastValue - firstValue;

        return new DerivedCandidate(
            group.Subject,
            DerivedPredicates.For(DerivationOperators.Delta, group.Predicate),
            MemoryDerivationMetadataExtensions.FormatDerivedNumber(delta),
            $"{MemoryDerivationMetadataExtensions.FormatDerivedNumber(lastValue)} ({lastFact.FactId}) "
            + $"- {MemoryDerivationMetadataExtensions.FormatDerivedNumber(firstValue)} ({firstFact.FactId})",
            [firstFact.FactId, lastFact.FactId],
            DerivationOperators.Delta);
    }

    internal static IReadOnlyList<(Fact Fact, decimal Value)>? NumericFacts(DerivationGroup group)
    {
        var parsed = new List<(Fact, decimal)>(group.Facts.Count);
        foreach (var fact in group.Facts)
        {
            if (!DerivedNumberParser.TryParse(fact.Object, out var value)) return null;
            parsed.Add((fact, value));
        }

        return parsed;
    }
}

/// <summary>The most recent value in the chain.</summary>
/// <remarks>
/// Distinct from supersession, which requires the writer to have <i>noticed</i> that one fact replaces
/// another. A chain of independently-extracted values never gets superseded, so "what is it now" has no
/// answer even though every value is present and correctly dated.
/// </remarks>
internal sealed class LatestEvaluator : IDerivationEvaluator
{
    public DerivationOperators Operator => DerivationOperators.Latest;

    public DerivedCandidate? Evaluate(DerivationGroup group)
    {
        // Needs a chain. With one fact "the latest value" is the fact, and materialising it would
        // duplicate an atom into the same budget it already occupies.
        if (group.Facts.Count < 2) return null;

        var latest = group.Facts[^1];
        var previous = group.Facts[^2];

        return new DerivedCandidate(
            group.Subject,
            DerivedPredicates.For(DerivationOperators.Latest, group.Predicate),
            latest.Object,
            $"most recent of {group.Facts.Count} values of {group.Predicate}: "
            + $"'{latest.Object}' ({latest.FactId}) as of "
            + $"{DerivationGroup.EffectiveAt(latest).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}, "
            + $"previously '{previous.Object}' ({previous.FactId})",
            [latest.FactId, previous.FactId],
            DerivationOperators.Latest);
    }
}

/// <summary>The total of the chain's numeric values. Allowlisted predicates only.</summary>
/// <remarks>
/// The allowlist is the operator's whole safety story. Summing is meaningful only for additive
/// quantities, and there is no way to tell an additive predicate from a non-additive one by looking at
/// it: adding three temperature readings produces a number whose arithmetic is exactly right and whose
/// meaning is nonsense — the kind of error no audit of the arithmetic can catch.
/// </remarks>
internal sealed class SumEvaluator : IDerivationEvaluator
{
    public DerivationOperators Operator => DerivationOperators.Sum;

    public DerivedCandidate? Evaluate(DerivationGroup group)
    {
        if (group.Facts.Count < 2) return null;
        if (!group.Options.AdditivePredicateKeys.Contains(group.PredicateKey, StringComparer.OrdinalIgnoreCase))
            return null;

        var numeric = DeltaEvaluator.NumericFacts(group);
        if (numeric is null) return null;

        var total = numeric.Sum(item => item.Value);
        var terms = string.Join(
            " + ",
            numeric.Select(item =>
                $"{MemoryDerivationMetadataExtensions.FormatDerivedNumber(item.Value)} ({item.Fact.FactId})"));

        return new DerivedCandidate(
            group.Subject,
            DerivedPredicates.For(DerivationOperators.Sum, group.Predicate),
            MemoryDerivationMetadataExtensions.FormatDerivedNumber(total),
            terms,
            [.. numeric.Select(item => item.Fact.FactId)],
            DerivationOperators.Sum);
    }
}

/// <summary>Elapsed time between the first and last dated value in one predicate chain.</summary>
/// <remarks>
/// <para>
/// Off by default, and the reason is about the data rather than the code: the current evaluation corpus
/// stamps <c>UnixEpoch + counter</c>, so a duration computed there is fiction with a plausible shape —
/// the most dangerous kind of wrong answer this feature could produce.
/// </para>
/// <para>
/// Requires <b>real</b> valid times on both ends, not the <c>created_at</c> fallback the rest of the
/// group ordering accepts: an interval between two extraction timestamps measures when the system was
/// told things, not when they happened.
/// </para>
/// </remarks>
internal sealed class DurationEvaluator : IDerivationEvaluator
{
    public DerivationOperators Operator => DerivationOperators.Duration;

    public DerivedCandidate? Evaluate(DerivationGroup group)
    {
        var dated = group.Facts.Where(f => f.ValidFrom is not null).ToList();
        if (dated.Count < 2) return null;

        var first = dated[0];
        var last = dated[^1];
        var span = last.ValidFrom!.Value - first.ValidFrom!.Value;
        var days = (int)Math.Round(span.TotalDays, MidpointRounding.AwayFromZero);
        if (days <= 0) return null;

        return new DerivedCandidate(
            group.Subject,
            DerivedPredicates.For(DerivationOperators.Duration, group.Predicate),
            $"P{days.ToString(CultureInfo.InvariantCulture)}D",
            $"{days} days between "
            + $"{first.ValidFrom!.Value.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} "
            + $"({first.FactId}) and "
            + $"{last.ValidFrom!.Value.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} "
            + $"({last.FactId})",
            [first.FactId, last.FactId],
            DerivationOperators.Duration);
    }
}

/// <summary>The distinct objects accumulated under one subject and predicate.</summary>
/// <remarks>
/// "Which three cities did I visit?" is the same shape as counting — a property of the set, sampled by
/// retrieval. Deduplication is case-insensitive because the graph canonicalises the same way; listing
/// "Paris" and "paris" as two cities would be a wrong answer produced by correct code.
/// </remarks>
internal sealed class SetEnumerationEvaluator : IDerivationEvaluator
{
    public DerivationOperators Operator => DerivationOperators.SetEnumeration;

    public DerivedCandidate? Evaluate(DerivationGroup group)
    {
        if (group.Facts.Count < 2) return null;

        // First spelling wins per distinct value, so the rendered list uses the words the user used.
        var distinct = new List<Fact>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fact in group.Facts)
        {
            if (string.IsNullOrWhiteSpace(fact.Object)) continue;
            if (seen.Add(fact.Object.Trim())) distinct.Add(fact);
        }

        // Two facts saying the same thing are a restatement, not a set.
        if (distinct.Count < 2) return null;

        var ordered = distinct
            .OrderBy(f => f.Object, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var capped = ordered.Take(Math.Max(1, group.Options.MaxEnumerationItems)).ToList();
        var truncated = ordered.Count - capped.Count;

        var values = string.Join("; ", capped.Select(f => f.Object.Trim()));
        var derivation =
            $"{ordered.Count} distinct values of {group.Predicate} "
            + $"({string.Join(", ", capped.Select(f => f.FactId))})";
        if (truncated > 0)
        {
            // Stated in the derivation, because a capped list read as complete is a wrong answer, and
            // the model has no other way to know the list was cut.
            derivation += $"; {truncated} more not listed";
        }

        return new DerivedCandidate(
            group.Subject,
            DerivedPredicates.For(DerivationOperators.SetEnumeration, group.Predicate),
            values,
            derivation,
            [.. capped.Select(f => f.FactId)],
            DerivationOperators.SetEnumeration);
    }
}
