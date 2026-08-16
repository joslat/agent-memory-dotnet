using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Core.Extraction.Derivation;

/// <summary>
/// One aggregate an evaluator computed, before it becomes a <see cref="Fact"/>.
/// </summary>
/// <param name="Subject">The group's subject, carried through unchanged.</param>
/// <param name="Predicate">
/// The derived predicate spelling, e.g. <c>count_of:visited_city</c>. Fixed by
/// <see cref="DerivedPredicates"/>, never invented per group — inventing one here would reproduce the
/// 421-predicates-over-700-facts problem inside the very feature built to work around it.
/// </param>
/// <param name="Object">The computed value, rendered.</param>
/// <param name="Derivation">
/// The arithmetic in words, e.g. <c>800 (a1) - 50 (b2)</c>. Rendered inline beside the value so the
/// model can check it rather than trust it.
/// </param>
/// <param name="InputFactIds">The facts this was computed from. Becomes <c>DERIVED_FROM</c> edges.</param>
/// <param name="Operator">Which arithmetic produced it.</param>
internal sealed record DerivedCandidate(
    string Subject,
    string Predicate,
    string Object,
    string Derivation,
    IReadOnlyList<string> InputFactIds,
    DerivationOperators Operator);

/// <summary>The fixed derived-predicate spellings.</summary>
/// <remarks>
/// A closed set, deliberately. Aggregation only works when two facts agree they are instances of the
/// same predicate; a feature that invented its own predicate names per group would be unable to
/// aggregate its own output.
/// </remarks>
internal static class DerivedPredicates
{
    public static string For(DerivationOperators op, string predicate) => op switch
    {
        DerivationOperators.Count => $"count_of:{predicate}",
        DerivationOperators.Delta => $"delta_of:{predicate}",
        DerivationOperators.Latest => $"latest_of:{predicate}",
        DerivationOperators.Sum => $"sum_of:{predicate}",
        DerivationOperators.Duration => $"interval_of:{predicate}",
        DerivationOperators.SetEnumeration => $"set_of:{predicate}",
        _ => throw new ArgumentOutOfRangeException(
            nameof(op), op, "No derived predicate spelling is defined for this operator."),
    };
}
