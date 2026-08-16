namespace AgentMemory.Abstractions.Options;

/// <summary>
/// The session accountant: materialises aggregates memory holds the <i>parts</i> of and never the whole.
/// </summary>
/// <remarks>
/// <para>
/// 16% of LongMemEval questions have a derived answer — a count, a difference, a latest-of-chain, a
/// duration, a list. The store holds <c>800</c> and <c>50</c>; the answer is <c>750</c>, and nothing
/// ever wrote it down. Every retrieval-side idea died at a saturated coverage ceiling; what is left
/// alive is answers that must be <b>computed</b> rather than found.
/// </para>
/// <para>
/// <b>Off by default, and off is byte-identical.</b> The accountant runs post-persistence, is LLM-free,
/// and touches no prompt bytes in either state; with the flag off it is never invoked, so the graph is
/// byte-identical too.
/// </para>
/// <para>
/// A mutable class rather than an init-only record, per the issue-#100 lesson: sub-options must be
/// settable through <c>configureMemory</c> or a host cannot configure them at all.
/// </para>
/// </remarks>
public sealed class DerivedMemoryOptions
{
    /// <summary>Materialises derived facts after each extraction batch. Default off.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Which arithmetic to perform. <see cref="DerivationOperators.Duration"/> and
    /// <see cref="DerivationOperators.Sum"/> are deliberately absent — see
    /// <see cref="DerivationOperators"/> for why each is opt-in.
    /// </summary>
    public DerivationOperators Operators { get; set; } =
        DerivationOperators.Count | DerivationOperators.Delta |
        DerivationOperators.Latest | DerivationOperators.SetEnumeration;

    /// <summary>
    /// Predicate keys whose values may be summed. Empty (the default) means
    /// <see cref="DerivationOperators.Sum"/> never runs.
    /// </summary>
    /// <remarks>
    /// An allowlist and not a heuristic: summing is only meaningful for additive quantities, and there
    /// is no reliable way to tell an additive predicate from a non-additive one by inspection. Summing
    /// three temperature readings produces a number that is wrong in a way no test would catch, because
    /// the arithmetic itself is correct.
    /// </remarks>
    public IList<string> AdditivePredicateKeys { get; } = new List<string>();

    /// <summary>Ceiling on derived facts written per extraction batch.</summary>
    public int MaxDerivedFactsPerBatch { get; set; } = 32;

    /// <summary>Ceiling on facts read per group. Bounds ingestion latency on a large chain.</summary>
    public int MaxGroupFanIn { get; set; } = 200;

    /// <summary>Ceiling on items listed by <see cref="DerivationOperators.SetEnumeration"/>.</summary>
    public int MaxEnumerationItems { get; set; } = 10;

    /// <summary>
    /// Confidence stamped on a derived fact.
    /// </summary>
    /// <remarks>
    /// An admitted guess. The arithmetic is exact, but a derived fact is only as good as the inputs it
    /// aggregated, and no principled number for that exists yet — the audit data is what should
    /// calibrate it.
    /// </remarks>
    public double DerivedFactConfidence { get; set; } = 0.9;
}
