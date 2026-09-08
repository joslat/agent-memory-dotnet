namespace AgentMemory.Abstractions.Options;

/// <summary>
/// How a fact search treats DERIVED facts — the session accountant's counts and sums.
/// </summary>
/// <remarks>
/// <para>
/// Derived facts are identified by <c>derivation_key</c>, which the derived-fact upsert writes and
/// no ordinary fact carries, so this is a property test rather than a heuristic.
/// </para>
/// <para>
/// <b>Why the distinction has to exist at all.</b> With the accountant enabled and no way to
/// separate its output, derived facts compete for the ordinary fact budget and displace the source
/// values they were computed from. Measured on the arithmetic vertical: **30% → 14%**, with
/// facts-per-question falling 35.4 → 27.6 as ~2,560 derived facts entered the same similarity pool.
/// A feature that writes answers into memory made the inputs harder to retrieve.
/// </para>
/// </remarks>
public enum DerivedFactMode
{
    /// <summary>
    /// Derived facts compete in the ordinary pool. The pre-existing behaviour, and the one whose
    /// cost was measured — kept as the default only so sealed measurements stay comparable.
    /// </summary>
    Include = 0,

    /// <summary>Derived facts are excluded. Strictly better than <see cref="Include"/> when nothing reads them.</summary>
    Exclude = 1,

    /// <summary>Only derived facts — used to give them their own budget rather than a share of one.</summary>
    Only = 2,
}
