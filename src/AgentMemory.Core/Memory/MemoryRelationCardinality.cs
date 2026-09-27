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
        if (resolved is not null && SingleValued.Value.Contains(resolved))
        {
            // 36.4 review. A relation that declares its present forms is single-valued only in them: "worked at" and
            // "used to work in" are history, and must neither replace the current employer nor be replaced by it.
            // Without a declaration, the relation's other forms keep the pre-existing behaviour (their own key only).
            var present = PresentForms.Value.TryGetValue(resolved, out var forms) ? forms : null;
            return present is null || present.Contains(canonical) ? resolved : null;
        }

        return PrefixRelation(canonical);
    }

    private static readonly Lazy<Dictionary<string, HashSet<string>>> PresentForms = new(() =>
        RelationVocabularyDocument.Load().Canonical
            .Where(entry => entry.Value.PresentForms.Count > 0)
            .ToDictionary(
                entry => MemoryTripleCanonicalizer.Canonical(entry.Key),
                entry => entry.Value.PresentForms.Select(MemoryTripleCanonicalizer.Canonical).ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal));

    /// <summary>
    /// A predicate naming a prefix-declared relation, as that relation: "favourite band", "has favourite band",
    /// "favourite band is" and "favorite band" are all <c>favourite band</c> (the first declared spelling).
    /// </summary>
    private static string? PrefixRelation(string canonical)
    {
        var words = canonical.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 0 && words[0] is "has" or "is" or "my") words.RemoveAt(0);
        // 36.4 review. "favourite bands are" holds several values: a plural is not a single-valued relation.
        if (words.Count > 0 && words[^1] == "are") return null;
        if (words.Count > 0 && words[^1] == "is") words.RemoveAt(words.Count - 1);
        if (words.Count < 2) return null;
        var last = words[^1];
        if (last.Length > 3 && last.EndsWith('s') && !last.EndsWith("ss", StringComparison.Ordinal) &&
            !last.EndsWith("us", StringComparison.Ordinal) && !last.EndsWith("is", StringComparison.Ordinal))
            return null;
        var prefix = SingleValuedPrefixes.Value.FirstOrDefault(p => string.Equals(p, words[0], StringComparison.Ordinal));
        return prefix is null ? null : string.Join(' ', [SingleValuedPrefixes.Value[0], .. words.Skip(1)]);
    }

    /// <summary>Every stored form of a prefix-declared relation: both spellings, with and without "has" / "is".</summary>
    private static IEnumerable<string> PrefixForms(string relation)
    {
        var thing = relation[(relation.IndexOf(' ', StringComparison.Ordinal) + 1)..];
        foreach (var prefix in SingleValuedPrefixes.Value)
        {
            yield return $"{prefix} {thing}";
            yield return $"has {prefix} {thing}";
            yield return $"{prefix} {thing} is";
            yield return $"my {prefix} {thing}";
        }
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
        var forms = PrefixRelation(canonical) is not null
            ? PrefixForms(relation)
            : PresentForms.Value.TryGetValue(relation, out var present)
                ? present
                : (IEnumerable<string>)[canonical];
        return [.. forms.Append(canonical).Distinct(StringComparer.Ordinal)];
    }

    private static readonly Lazy<string[]> SingleValuedPrefixes = new(() =>
        RelationVocabularyDocument.Load().SingleValuedPrefixes.Keys
            .Select(MemoryTripleCanonicalizer.Canonical)
            .Where(key => key.Length > 0)
            .ToArray());

    /// <summary>The declared functional relations, for reporting and for the guard test.</summary>
    internal static IReadOnlyCollection<string> SingleValuedPredicates => SingleValued.Value;
}
