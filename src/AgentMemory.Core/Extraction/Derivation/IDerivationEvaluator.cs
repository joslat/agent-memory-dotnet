using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Core.Extraction.Derivation;

/// <summary>
/// One operator's arithmetic over one <c>(subject, predicate, owner)</c> group.
/// </summary>
/// <remarks>
/// <para>
/// An interface rather than a switch, so the reachability guard can assert by reflection that every
/// <see cref="DerivationOperators"/> flag has exactly one implementation. An operator flag a host can
/// set and no code reads is the fifteen-times-repeated defect in this codebase; here it would be
/// especially quiet, because the symptom is simply that certain aggregates never appear.
/// </para>
/// <para>
/// Implementations are <b>pure</b>: no clock, no repository, no model. Everything they need arrives in
/// <see cref="DerivationGroup"/>, which is what makes the arithmetic auditable — a derived value can be
/// recomputed out-of-band from its recorded inputs and compared exactly.
/// </para>
/// </remarks>
internal interface IDerivationEvaluator
{
    /// <summary>Which flag turns this evaluator on. Exactly one per implementation.</summary>
    DerivationOperators Operator { get; }

    /// <summary>The aggregate, or <see langword="null"/> when this group yields none.</summary>
    DerivedCandidate? Evaluate(DerivationGroup group);
}

/// <summary>
/// The live, non-derived facts sharing one subject and predicate, in the order they became true.
/// </summary>
/// <param name="Subject">The group's subject as first observed.</param>
/// <param name="Predicate">The group's predicate as first observed.</param>
/// <param name="PredicateKey">The canonical predicate key — what the allowlist matches on.</param>
/// <param name="Facts">
/// Ordered by <c>coalesce(valid_from, created_at)</c> ascending. The order is the arithmetic: a delta
/// computed over an unordered group is a subtraction of two arbitrary members.
/// </param>
/// <param name="Options">The caps and allowlists this run must respect.</param>
internal sealed record DerivationGroup(
    string Subject,
    string Predicate,
    string PredicateKey,
    IReadOnlyList<Fact> Facts,
    DerivedMemoryOptions Options)
{
    /// <summary>When a fact became true, falling back to when it was learned.</summary>
    /// <remarks>
    /// Valid time first, because "learned yesterday about 2019" must sort as 2019. The fallback matters
    /// as much: most extracted facts carry no valid time at all, and dropping them would leave every
    /// group too small to aggregate.
    /// </remarks>
    public static DateTimeOffset EffectiveAt(Fact fact) => fact.ValidFrom ?? fact.CreatedAtUtc;
}
