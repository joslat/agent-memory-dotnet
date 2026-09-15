namespace AgentMemory.LongMemEval;

/// <summary>
/// A SECOND dense retriever's headroom per shape, derived from AgentEval's own compare tool —
/// because the flag we read is conditional on a retriever, and one retriever is not a condition,
/// it is an assumption.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The sidecar's <c>discriminates_under_dense</c> is measured with
/// <c>text-embedding-ada-002</c>, a 2022 model, and until 2026-09-14 the sidecar did not say so —
/// it said "azure-openai-embeddings", which every embedding model Azure has ever served satisfies.
/// The coordinator's 2026-09-15 ruling therefore DEMOTES that boolean from a filter to a
/// three-class read: the flag <b>flips on 4 of 35 shapes</b> between two published embedders, and
/// <b>neither of them is ours</b>.
/// </para>
/// <para>
/// <b>DERIVED, NOT PUBLISHED.</b> Every number here comes from
/// <c>tools/typedmemeval_retriever_compare.py</c> in the AgentEval repository, run at zero API cost
/// over vectors already banked:
/// <code>
/// python tools/typedmemeval_retriever_compare.py ///     --models text-embedding-ada-002,text-embedding-3-small
/// </code>
/// If AgentEval later co-publish the second column in the sidecars — which the coordinator has
/// suggested to them — <b>delete this file and read the package instead</b>. A derived table is a
/// snapshot; a published one cannot drift from the corpus it describes.
/// </para>
/// <para>
/// <b>Two checks stand behind it, both re-run as tests.</b> The tool's self-check
/// (<c>--models X,X</c>) must report a spread of exactly <c>0.000000</c>, or the comparison is
/// measuring plumbing rather than models. And the tool's ada column must reproduce the package's
/// own <c>predicted_headroom_dense</c> on all 35 shapes — it does, which is what licenses using its
/// second column at all.
/// </para>
/// </remarks>
internal static class TypedMemEvalDenseSecondOpinion
{
    /// <summary>
    /// The headroom above which AgentEval call a shape discriminating. <b>INFERRED, not published.</b>
    /// </summary>
    /// <remarks>
    /// The sidecar ships the boolean and the headroom but not the rule joining them. This value
    /// reproduces the published boolean on <b>all 35 shapes</b>, across a clean separating gap —
    /// every FALSE shape sits at or below 0.100 and every TRUE shape at or above 0.167 — and a test
    /// re-derives all 35 so a changed rule upstream fails loudly instead of silently reclassifying
    /// the second column. It is still an inference, and it is labelled as one wherever it is used.
    /// </remarks>
    internal const double DiscriminationThreshold = 0.10;

    /// <summary>Per-shape headroom under both published retrievers, keyed <c>vertical/shape</c>.</summary>
    private static readonly IReadOnlyDictionary<string, DenseHeadroomPair> Headroom =
        new Dictionary<string, DenseHeadroomPair>(StringComparer.Ordinal)
        {
        ["arithmetic/count"] = new(AdaHeadroom: 0.2860, SmallHeadroom: 0.4290),
        ["arithmetic/delta"] = new(AdaHeadroom: 0.7000, SmallHeadroom: 0.5000),
        ["arithmetic/duration"] = new(AdaHeadroom: 0.5000, SmallHeadroom: 0.5000),
        ["arithmetic/sum"] = new(AdaHeadroom: 0.4290, SmallHeadroom: 0.2140),
        ["bitemporal/belief-at-instant"] = new(AdaHeadroom: 0.2500, SmallHeadroom: 0.3060),
        ["bitemporal/correction-depth"] = new(AdaHeadroom: 0.3750, SmallHeadroom: 0.2920),
        ["conjunction/alias-then-count"] = new(AdaHeadroom: 0.7330, SmallHeadroom: 0.6670),
        ["conjunction/conditional-branch"] = new(AdaHeadroom: 1.0000, SmallHeadroom: 1.0000),
        ["conjunction/order-then-value"] = new(AdaHeadroom: 1.0000, SmallHeadroom: 1.0000),
        ["conjunction/value-then-count"] = new(AdaHeadroom: 0.9500, SmallHeadroom: 0.9500),
        ["episodic/assistant-stated"] = new(AdaHeadroom: 0.0000, SmallHeadroom: 0.0000),
        ["episodic/list-order"] = new(AdaHeadroom: 0.6000, SmallHeadroom: 0.8000),
        ["episodic/participant-attribution"] = new(AdaHeadroom: 0.5330, SmallHeadroom: 0.4670),
        ["forgetting/invalidated"] = new(AdaHeadroom: 0.1000, SmallHeadroom: 0.0000),
        ["forgetting/still-valid"] = new(AdaHeadroom: 0.3330, SmallHeadroom: 0.0670),
        ["procedural/amended-step"] = new(AdaHeadroom: 0.8500, SmallHeadroom: 0.7000),
        ["procedural/precondition"] = new(AdaHeadroom: 0.6500, SmallHeadroom: 0.7000),
        ["procedural/retired-step"] = new(AdaHeadroom: 0.8500, SmallHeadroom: 0.8000),
        ["procedural/step-order"] = new(AdaHeadroom: 1.0000, SmallHeadroom: 1.0000),
        ["prospective/due-later-reminder"] = new(AdaHeadroom: 0.0000, SmallHeadroom: 0.0000),
        ["prospective/due-window"] = new(AdaHeadroom: 1.0000, SmallHeadroom: 0.8890),
        ["prospective/expiring-validity"] = new(AdaHeadroom: 0.0000, SmallHeadroom: 0.0000),
        ["prospective/not-yet-true"] = new(AdaHeadroom: 0.0000, SmallHeadroom: 0.0000),
        ["prospective/seed-carry-over"] = new(AdaHeadroom: 0.0830, SmallHeadroom: 0.0000),
        ["semantic/co-reference"] = new(AdaHeadroom: 0.5330, SmallHeadroom: 0.4000),
        ["semantic/current-value"] = new(AdaHeadroom: 0.2500, SmallHeadroom: 0.3000),
        ["semantic/source-attribution"] = new(AdaHeadroom: 0.0000, SmallHeadroom: 0.0000),
        ["temporal/interval-position"] = new(AdaHeadroom: 0.2000, SmallHeadroom: 0.0000),
        ["temporal/occurrence-order"] = new(AdaHeadroom: 0.8000, SmallHeadroom: 0.7000),
        ["temporal/recency"] = new(AdaHeadroom: 0.4670, SmallHeadroom: 0.2000),
        ["workingmemory/distance-15"] = new(AdaHeadroom: 0.2500, SmallHeadroom: 0.3330),
        ["workingmemory/distance-25"] = new(AdaHeadroom: 0.1670, SmallHeadroom: 0.0830),
        ["workingmemory/distance-40"] = new(AdaHeadroom: 0.0830, SmallHeadroom: 0.4170),
        ["workingmemory/distance-60"] = new(AdaHeadroom: 0.2500, SmallHeadroom: 0.4170),
        ["workingmemory/distance-8"] = new(AdaHeadroom: 0.3330, SmallHeadroom: 0.1670),
        };

    /// <summary>The pair for one shape, or null when this table does not describe it.</summary>
    internal static DenseHeadroomPair? For(string verticalSlug, string shape) =>
        Headroom.TryGetValue($"{verticalSlug}/{shape}", out var pair) ? pair : null;

    /// <summary>Every shape this table covers — used by the tests that verify it against the package.</summary>
    internal static IReadOnlyDictionary<string, DenseHeadroomPair> All => Headroom;
}

/// <summary>Headroom for one shape under each published dense retriever.</summary>
/// <param name="AdaHeadroom">Under <c>text-embedding-ada-002</c> — the column the sidecar publishes.</param>
/// <param name="SmallHeadroom">Under <c>text-embedding-3-small</c> — derived here, not published.</param>
internal readonly record struct DenseHeadroomPair(double AdaHeadroom, double SmallHeadroom)
{
    private static bool Discriminates(double headroom) =>
        headroom > TypedMemEvalDenseSecondOpinion.DiscriminationThreshold + 1e-9;

    /// <summary>Whether ada-002 ranks systems on this shape.</summary>
    internal bool DiscriminatesUnderAda => Discriminates(AdaHeadroom);

    /// <summary>Whether 3-small ranks systems on this shape.</summary>
    internal bool DiscriminatesUnderSmall => Discriminates(SmallHeadroom);

    /// <summary>The three-class read the 2026-09-15 ruling requires.</summary>
    internal DenseRankingClass Class => (DiscriminatesUnderAda, DiscriminatesUnderSmall) switch
    {
        (true, true) => DenseRankingClass.Robust,
        (false, false) => DenseRankingClass.NonRanking,
        _ => DenseRankingClass.RetrieverSensitive,
    };
}

/// <summary>How much confidence a shape's ranking power deserves.</summary>
internal enum DenseRankingClass
{
    /// <summary>Discriminates under BOTH published retrievers — ranks us with confidence.</summary>
    Robust,

    /// <summary>
    /// Flips between the two. Read cautiously and <b>never load-bearing for a ship/no-ship decision
    /// alone</b> — the flag's value here is a fact about the embedder, not about the engine.
    /// </summary>
    RetrieverSensitive,

    /// <summary>Fails under both — non-ranking for us, as before.</summary>
    NonRanking,
}
