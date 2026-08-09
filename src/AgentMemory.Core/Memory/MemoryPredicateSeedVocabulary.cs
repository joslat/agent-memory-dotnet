namespace AgentMemory.Core.Memory;

/// <summary>
/// The starting set of relation names offered to extraction.
/// </summary>
/// <remarks>
/// <para>
/// Curated once rather than mined per run, for two reasons. A vocabulary that accumulated <i>during</i>
/// a run would make each call's prompt depend on which concurrent extraction finished first, so the
/// same input could produce different prompts — the precise property that made an earlier
/// Structured score sequence unattributable. And a reviewed list can be checked by a human for the
/// one mistake that matters here: silently omitting one side of an opposing pair.
/// </para>
/// <para>
/// Drawn from relations actually observed in extracted graphs. Deliberately small: it is injected
/// into every extraction prompt, and a list approaching the 421-predicates-per-700-facts figure that
/// motivated it would consume the budget it exists to improve.
/// </para>
/// <para>
/// <b>Opposing relations are both present by design.</b> <c>bought</c>/<c>sold</c> and
/// <c>likes</c>/<c>dislikes</c> are one embedding threshold apart and mean opposite things; offering
/// only one would invite the extractor to collapse them and invert facts.
/// </para>
/// </remarks>
public static class MemoryPredicateSeedVocabulary
{
    /// <summary>
    /// Derived from the single relation table, never authored separately.
    /// </summary>
    /// <remarks>
    /// This list was previously maintained by hand alongside the query lexicon, and the two drifted:
    /// **13 relations became resolvable at query time that the extractor was never offered**, so the
    /// graph could not contain them however well retrieval worked. `assembled` was one of them, which
    /// is why assembly was filed under `completed` and the furniture question could not be answered
    /// from the graph. One relation known to two layers must have one definition.
    /// <para>
    /// Only the canonical keys cross over. Surface forms stay read-side: they never enter an
    /// extraction prompt, where they would cost tokens on every call and invite the extractor to
    /// choose inconsistently between <c>buy</c>, <c>buys</c> and <c>purchased</c> - the opposite of
    /// the consolidation this vocabulary exists to produce.
    /// </para>
    /// </remarks>
    private static readonly string[] Seed =
        [.. MemoryRelationSeedTable.Table.Keys
            .Where(relation => !MemoryRelationSeedTable.RetiredRelations.Contains(relation))
            .OrderBy(relation => relation, StringComparer.Ordinal)];

    /// <summary>
    /// Content hash of this vocabulary, for recording which table produced a given extracted graph.
    /// </summary>
    /// <remarks>
    /// This list is injected into every extraction prompt, so changing it changes what is stored. Two
    /// graphs built under different vocabularies are not comparable, and without this the artifact
    /// would not say which one produced it.
    /// </remarks>
    public static string Fingerprint { get; } = MemoryVocabularyFingerprint.Of(Seed);

    /// <summary>A vocabulary pre-populated with the curated seed relations.</summary>
    public static MemoryPredicateVocabulary Create()
    {
        var vocabulary = new MemoryPredicateVocabulary();
        foreach (var predicate in Seed)
            vocabulary.Admit(predicate);
        return vocabulary;
    }
}
