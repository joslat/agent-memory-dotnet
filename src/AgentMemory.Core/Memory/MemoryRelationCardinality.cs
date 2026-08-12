namespace AgentMemory.Core.Memory;

/// <summary>
/// Which relations hold at most one live value per subject.
/// </summary>
/// <remarks>
/// <para>
/// The question write-time supersession has to answer is <b>"does this new assertion replace the old
/// one, or join it?"</b> — and getting it wrong in the replacing direction is a data-shaped defect:
/// storing "likes tea" would drop "likes coffee" from live recall, with nothing to indicate that a
/// true fact had been closed.
/// </para>
/// <para>
/// So this answers <see langword="false"/> for everything it has not been explicitly told about.
/// Every <c>event</c> relation is additive by nature — a person attends many things, buys many things
/// — and most <c>state</c> relations are too. The functional set is a small, reviewed handful
/// declared in the vocabulary artifact beside each relation, with the argument for it recorded next to
/// it.
/// </para>
/// <para>
/// Matching is on the <b>canonical</b> predicate key, so a fact stored as <c>lived in</c> or
/// <c>lives_in</c> resolves to the same relation the declaration names. An unrecognised predicate —
/// one the extractor invented outside the vocabulary — is multi-valued, which is the safe answer for
/// something nobody has reviewed.
/// </para>
/// </remarks>
internal static class MemoryRelationCardinality
{
    private const string Single = "single";

    private static readonly Lazy<HashSet<string>> SingleValued = new(() =>
    {
        var document = RelationVocabularyDocument.Load();
        return document.Canonical
            .Where(entry => string.Equals(entry.Value.Cardinality, Single, StringComparison.OrdinalIgnoreCase))
            .Select(entry => MemoryTripleCanonicalizer.Canonical(entry.Key))
            .Where(key => key.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    });

    /// <summary>
    /// Whether <paramref name="predicate"/> holds at most one live value per subject.
    /// </summary>
    /// <remarks>
    /// Resolves surface forms through <see cref="MemoryRelationLexicon"/> first, so a predicate the
    /// extractor wrote as <c>lived in</c> is recognised as the relation the vocabulary declares. False
    /// for anything unrecognised.
    /// </remarks>
    internal static bool IsSingleValued(string? predicate)
    {
        if (string.IsNullOrWhiteSpace(predicate)) return false;
        var canonical = MemoryTripleCanonicalizer.Canonical(predicate);
        if (canonical.Length == 0) return false;
        if (SingleValued.Value.Contains(canonical)) return true;

        // A surface form of a functional relation is that relation. Without this, "lived in" would
        // accumulate beside "lives in" and the two would both be live, which is the accumulation this
        // exists to stop.
        var resolved = MemoryRelationLexicon.Default.Resolve(canonical);
        return resolved is not null && SingleValued.Value.Contains(resolved);
    }

    /// <summary>The declared functional relations, for reporting and for the guard test.</summary>
    internal static IReadOnlyCollection<string> SingleValuedPredicates => SingleValued.Value;
}
