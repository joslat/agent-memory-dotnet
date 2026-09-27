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
    internal static bool IsSingleValued(string? predicate) => Relation(predicate) is not null;

    /// <summary>
    /// 36.4. The single-valued relation <paramref name="predicate"/> is a form of, or null when it is multi-valued.
    /// </summary>
    /// <remarks>
    /// Stored forms resolve through <see cref="MemoryRelationLexicon.ResolveStored"/>, not the question-side
    /// resolver: "works for" is stored under <c>works at</c>, and the question side refuses it. A predicate that
    /// starts with a declared prefix (<c>favourite band</c>) is its own single-valued relation.
    /// </remarks>
    internal static string? Relation(string? predicate)
    {
        if (string.IsNullOrWhiteSpace(predicate)) return null;
        var canonical = MemoryTripleCanonicalizer.Canonical(predicate);
        if (canonical.Length == 0) return null;
        if (SingleValued.Value.Contains(canonical)) return canonical;

        // A surface form of a functional relation is that relation. Without this, "lived in" would
        // accumulate beside "lives in" and the two would both be live, which is the accumulation this
        // exists to stop.
        var resolved = MemoryRelationLexicon.Default.ResolveStored(canonical);
        if (resolved is not null && SingleValued.Value.Contains(resolved)) return resolved;

        return SingleValuedPrefixes.Value.Any(prefix =>
            canonical.Length > prefix.Length && canonical.StartsWith(prefix, StringComparison.Ordinal) &&
            !char.IsLetterOrDigit(canonical[prefix.Length]))
            ? canonical
            : null;
    }

    /// <summary>
    /// 36.4. Every stored predicate key a new value of <paramref name="predicate"/> replaces: all the forms of its
    /// single-valued relation ("works for" replaces "works at" and "worked for"), or the predicate's own key.
    /// </summary>
    internal static IReadOnlyList<string> ReplacedKeys(string? predicate)
    {
        var canonical = MemoryTripleCanonicalizer.Canonical(predicate);
        var relation = Relation(predicate);
        if (relation is null) return canonical.Length == 0 ? [] : [canonical];
        return [.. MemoryRelationLexicon.Default.StoredFormsOf(relation).Append(canonical).Distinct(StringComparer.Ordinal)];
    }

    private static readonly Lazy<string[]> SingleValuedPrefixes = new(() =>
        RelationVocabularyDocument.Load().SingleValuedPrefixes.Keys
            .Select(MemoryTripleCanonicalizer.Canonical)
            .Where(key => key.Length > 0)
            .ToArray());

    /// <summary>The declared functional relations, for reporting and for the guard test.</summary>
    internal static IReadOnlyCollection<string> SingleValuedPredicates => SingleValued.Value;
}
