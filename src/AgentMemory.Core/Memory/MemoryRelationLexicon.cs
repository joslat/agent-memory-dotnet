using System.Collections.Frozen;

namespace AgentMemory.Core.Memory;

/// <summary>
/// Resolves the verbs a question uses onto the canonical predicates a graph stores.
/// </summary>
/// <remarks>
/// <para>
/// J2.1. Predicate expansion makes one relation <i>complete</i>, but it can only expand predicates
/// that similarity already surfaced in the top-K. A question naming several relations
/// ("did I buy, assemble, sell, or fix") therefore reaches only whichever of them retrieval happened
/// to nominate. This supplies the relations from the <i>question</i> instead.
/// </para>
/// <para>
/// <b>Read-side only, and that asymmetry is the safety argument.</b> Clustering predicates over stored
/// facts was rejected because merging <c>bought</c> onto <c>sold</c> corrupts meaning irreversibly. A
/// wrong entry here costs precision on one query and can never alter a stored fact. Fuzzy is
/// unacceptable at write time and tolerable at read time.
/// </para>
/// <para>
/// The authored direction is <c>canonical → surface forms</c>; the lookup index is <b>derived</b>, so
/// the two can never disagree. Irregular forms are listed explicitly rather than inferred, and
/// suffix stripping is only a fallback for forms the table does not list.
/// </para>
/// </remarks>
internal sealed class MemoryRelationLexicon
{
    /// <summary>Longest multi-word surface form, so the harvester knows its window.</summary>
    private const int MaximumPhraseWords = 4;

    /// <summary>
    /// Words that mark a sentence as being about the owner of the memory rather than an instruction.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow. Second person is excluded because "can you tell me…" is the commonest
    /// assistant-request opener there is, and admitting it would reopen the exact hole this closes.
    /// </remarks>
    private static readonly FrozenSet<string> FirstPersonMarkers =
        new[] { "i", "me", "my", "mine", "myself", "we", "us", "our", "ours" }
            .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Words that mark a sentence as a question rather than an instruction.
    /// </summary>
    /// <remarks>
    /// Needed because a memory question need not mention its owner: "How many babies were born to
    /// friends and family" is about the user's history and contains no first person at all. It is
    /// also the one question predicate expansion is measured to flip, so a gate that dropped it would
    /// have traded away the only proven win in this track.
    /// </remarks>
    private static readonly FrozenSet<string> Interrogatives =
        new[]
        {
            "what", "when", "where", "who", "whom", "whose", "which", "how", "why",
            "did", "do", "does", "was", "were", "is", "are", "has", "have", "had", "can", "could"
        }.ToFrozenSet(StringComparer.Ordinal);

    private readonly FrozenDictionary<string, string> _surfaceToCanonical;
    private readonly FrozenDictionary<string, string[]> _canonicalToStoredForms;
    private readonly FrozenSet<string> _canonical;
    private readonly FrozenSet<string> _queryStopForms;

    private MemoryRelationLexicon(
        FrozenDictionary<string, string> surfaceToCanonical,
        FrozenDictionary<string, string[]> canonicalToStoredForms,
        FrozenSet<string> canonical,
        FrozenSet<string> queryStopForms,
        IReadOnlyList<string> ambiguousSurfaceForms)
    {
        _queryStopForms = queryStopForms;
        _surfaceToCanonical = surfaceToCanonical;
        _canonicalToStoredForms = canonicalToStoredForms;
        _canonical = canonical;
        AmbiguousSurfaceForms = ambiguousSurfaceForms;
    }

    internal static MemoryRelationLexicon Default { get; } = Build(MemoryRelationSeedTable.Table);

    /// <summary>
    /// Surface forms that were claimed by more than one canonical relation and therefore dropped.
    /// </summary>
    /// <remarks>
    /// Exposed rather than silently discarded: a duplicate in a hand-authored table is an authoring
    /// mistake, and a test asserts this is empty for the shipped table. Dropping keeps the runtime
    /// safe; exposing keeps the mistake visible.
    /// </remarks>
    internal IReadOnlyList<string> AmbiguousSurfaceForms { get; }

    internal IReadOnlyCollection<string> CanonicalRelations => _canonical;

    /// <summary>Resolves one surface form, or null when the table does not know it.</summary>
    /// <remarks>
    /// A null is load-bearing: it is what lets the caller fall back to today's top-K-derived
    /// predicates, which is what makes this incapable of being worse than current behaviour.
    /// </remarks>
    internal string? Resolve(string? surfaceForm)
    {
        var normalized = MemoryTripleCanonicalizer.Canonical(surfaceForm);
        if (normalized.Length == 0 || _queryStopForms.Contains(normalized))
            return null;
        if (_surfaceToCanonical.TryGetValue(normalized, out var canonical))
            return canonical;

        // Fallback only. Listed irregulars have already matched above.
        var stemmed = Stem(normalized);
        if (stemmed is null)
            return null;

        // The stem is re-checked against the stop list, not just the raw form. Without this a
        // suppressed bare verb is handed straight back through its own inflections - "works" and
        // "working" both stem to a suppressed "work" - which silently defeats every suppression
        // decision in the vocabulary rather than only the one being read.
        if (_queryStopForms.Contains(stemmed))
            return null;

        return _surfaceToCanonical.TryGetValue(stemmed, out var stemMatch) ? stemMatch : null;
    }


    /// <summary>
    /// Whether a <b>stored</b> predicate key is a form the vocabulary knows.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="Resolve"/>. That method answers a query-side question and rejects
    /// stop forms on purpose, so a question mentioning "is" does not expand into the whole graph.
    /// Those same forms are perfectly legitimate <i>stored</i> predicates — the measured graph holds
    /// <c>has</c> with 1,583 facts and <c>is</c> with 1,118 — and scoring storage coverage with the
    /// query-side method reports them as unknown vocabulary when they are nothing of the kind.
    /// <para>
    /// Written for J1.5 gate 1 after exactly that mistake made the gate read 81.5% when the genuine
    /// gap was far smaller. The two questions are different and need different methods; the fix is
    /// not to make the stop forms resolvable, which would defeat the suppression they exist for.
    /// </para>
    /// </remarks>
    internal bool IsKnownStoredForm(string? predicateKey)
    {
        var normalized = MemoryTripleCanonicalizer.Canonical(predicateKey);
        if (normalized.Length == 0)
            return false;
        if (_surfaceToCanonical.ContainsKey(normalized) || _canonical.Contains(normalized))
            return true;

        var stemmed = Stem(normalized);
        return stemmed is not null &&
               (_surfaceToCanonical.ContainsKey(stemmed) || _canonical.Contains(stemmed));
    }

    /// <summary>
    /// Every form of a relation that could appear as a stored <c>predicate_key</c>, including itself.
    /// </summary>
    /// <remarks>
    /// The write-side canonicalizer folds case and separators but deliberately never morphology, so
    /// one relation is stored under several keys: the measured graph holds <c>planned</c> with 839
    /// facts and <c>plans</c> with 14 as separate keys. Expanding on the canonical name alone would
    /// silently miss the smaller bucket - precisely the completeness failure expansion exists to
    /// prevent. An unknown relation yields just itself, so callers need no special case.
    /// </remarks>
    internal IReadOnlyList<string> StoredFormsOf(string? relation)
    {
        var canonical = MemoryTripleCanonicalizer.Canonical(relation);
        if (canonical.Length == 0)
            return [];
        return _canonicalToStoredForms.TryGetValue(canonical, out var forms)
            ? forms
            : [canonical];
    }

    /// <summary>
    /// Harvests every relation a question names, distinct and in order of first appearance.
    /// </summary>
    internal IReadOnlyList<string> ResolveQuestion(string? question)
    {
        if (string.IsNullOrWhiteSpace(question))
            return [];

        // Tokenized on non-letters rather than through the predicate canonicalizer, which folds
        // separators but leaves sentence punctuation intact: "buy," would then never match "buy".
        var words = new string(question
                .Select(character => char.IsLetter(character) ? char.ToLowerInvariant(character) : ' ')
                .ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return [];

        // Reject the imperative-instruction shape. Two independent reviews measured the same bound:
        // per-form suppression cannot reach it, because `create`, `build`, `save`, `read` and `start`
        // are all legitimate relations that cannot be deleted, yet "Create a summary" is not a
        // question about anyone's past. It is a property of the sentence, not of any single verb,
        // which is why no amount of per-form editing reached it.
        //
        // The discriminator is mood, NOT first person. A first-person test was written first and
        // measured against the benchmark before being trusted: it would have blocked "How many babies
        // were born to friends and family members" - a question with no first-person marker at all,
        // and the single question that predicate expansion is proven to flip. An imperative is a bare
        // verb with no subject, so the test is that the sentence OPENS on a relation verb while
        // carrying neither an interrogative nor a first-person marker.
        if (Resolve(words[0]) is not null &&
            !words.Any(FirstPersonMarkers.Contains) &&
            !words.Any(Interrogatives.Contains))
        {
            return [];
        }

        var resolved = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < words.Length; index++)
        {
            // Longest phrase first: "is interested in" must win over "is", or a multi-word relation
            // can never be reached.
            for (var length = Math.Min(MaximumPhraseWords, words.Length - index); length >= 1; length--)
            {
                var phrase = string.Join(' ', words, index, length);
                if (Resolve(phrase) is not { } canonical)
                    continue;
                if (seen.Add(canonical))
                    resolved.Add(canonical);
                index += length - 1;
                break;
            }
        }

        return resolved;
    }

    private static MemoryRelationLexicon Build(
        IReadOnlyDictionary<string, string[]> table)
    {
        var surfaceToCanonical = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguous = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var (canonicalRaw, surfaceForms) in table)
        {
            var canonical = MemoryTripleCanonicalizer.Canonical(canonicalRaw);
            // A relation always resolves to itself, so the table never has to repeat its own name.
            foreach (var surface in surfaceForms.Append(canonical))
            {
                var key = MemoryTripleCanonicalizer.Canonical(surface);
                if (key.Length == 0)
                    continue;
                if (surfaceToCanonical.TryGetValue(key, out var existing) &&
                    !string.Equals(existing, canonical, StringComparison.Ordinal))
                {
                    ambiguous.Add(key);
                    continue;
                }

                surfaceToCanonical[key] = canonical;
            }
        }

        // A form claimed by two relations is removed outright rather than awarded to whichever was
        // authored first, which would make the result depend on table order.
        foreach (var key in ambiguous)
            surfaceToCanonical.Remove(key);

        // The inverse index is DERIVED from the surviving forward map, never authored separately, so
        // the two directions cannot disagree and a dropped ambiguous form stays dropped in both.
        var canonicalToStoredForms = surfaceToCanonical
            .GroupBy(pair => pair.Value, StringComparer.Ordinal)
            .ToFrozenDictionary(
                group => group.Key,
                group => group.Select(pair => pair.Key)
                    .OrderBy(form => form, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);

        return new MemoryRelationLexicon(
            surfaceToCanonical.ToFrozenDictionary(StringComparer.Ordinal),
            canonicalToStoredForms,
            table.Keys.Select(MemoryTripleCanonicalizer.Canonical)
                .ToFrozenSet(StringComparer.Ordinal),
            MemoryRelationSeedTable.QueryStopForms
                .Select(MemoryTripleCanonicalizer.Canonical)
                .ToFrozenSet(StringComparer.Ordinal),
            [.. ambiguous]);
    }

    /// <summary>
    /// Light, deterministic suffix stripping for forms the table does not list.
    /// </summary>
    /// <remarks>
    /// The length guards are not cosmetic. <c>is</c> is the single most common predicate in the
    /// measured graph - 1,213 facts, 26% of all of them - and naive <c>-s</c> stripping would reduce
    /// it to <c>i</c> and lose a quarter of the graph.
    /// </remarks>
    private static string? Stem(string value)
    {
        if (value.Length <= 4)
            return null;

        if (value.EndsWith("ing", StringComparison.Ordinal) && value.Length > 6)
            return value[..^3];
        if (value.EndsWith("ed", StringComparison.Ordinal) && value.Length > 5)
            return value[..^2];
        if (value.EndsWith("es", StringComparison.Ordinal) && value.Length > 5)
            return value[..^2];
        if (value.EndsWith('s') &&
            !value.EndsWith("ss", StringComparison.Ordinal) &&
            value.Length > 4)
        {
            return value[..^1];
        }

        return null;
    }
}
