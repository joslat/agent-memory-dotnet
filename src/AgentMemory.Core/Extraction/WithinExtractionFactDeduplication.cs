using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Core.Memory;

namespace AgentMemory.Core.Extraction;

/// <summary>Merges equivalent statements conservatively while preserving source and validity semantics.</summary>
internal static class WithinExtractionFactDeduplication
{
    /// <summary>
    /// I-6: one fact per statement within an extraction. Two facts are the same statement in other words
    /// when they are at least <paramref name="threshold"/> similar AND <see cref="MayBeOneStatement"/>:
    /// similarity alone scores "has 2 kids" / "has 3 kids" and "is vegetarian" / "is not vegetarian" as
    /// near-identical. The kept phrasing is the user's over the assistant's, then the more confident, then
    /// the first; it takes over a validity window or source turn only the other had, unless only the
    /// assistant said it.
    /// Each dropped fact gets a <see cref="IngestionItemStatus.Skipped"/> outcome naming the kept one, and
    /// the span records how many were merged (<c>memory.persist.facts_merged</c>).
    /// </summary>
    internal static (IReadOnlyList<PreparedFact> Kept, IReadOnlyList<IngestionItemOutcome> Merged) Merge(
        IReadOnlyList<PreparedFact> facts, double threshold)
    {
        var kept = new List<PreparedFact>(facts.Count);
        var merged = new List<IngestionItemOutcome>();
        foreach (var fact in facts)
        {
            var twin = fact.Embedding is { Length: > 0 }
                ? kept.FindIndex(k => k.Embedding is { Length: > 0 } && k.Embedding.Length == fact.Embedding.Length &&
                                      MayBeOneStatement(k.Item, fact.Item) &&
                                      Resolution.SemanticMatchEntityMatcher.CosineSimilarity(k.Embedding, fact.Embedding) >= threshold)
                : -1;
            if (twin < 0)
            {
                kept.Add(fact);
                continue;
            }
            var (winner, loser) = Prefer(fact.Item, kept[twin].Item) ? (fact, kept[twin]) : (kept[twin], fact);
            // What only the assistant said is not carried into the user's words.
            var carry = !IsAssistant(loser.Item);
            // At the place of its latest telling (this one): what was said last is what the person holds now.
            kept.RemoveAt(twin);
            kept.Add(winner with
            {
                AdditionalClosings =
                [
                    .. winner.AdditionalClosings,
                    .. carry ? loser.AdditionalClosings : [],
                    .. carry && !string.IsNullOrWhiteSpace(loser.Item.ReplacesId)
                        ? new[] { (loser.Item.ReplacesId!, loser.Item.ReplacementIsCorrection) } : [],
                ],
                Item = winner.Item with
                {
                    ValidFrom = winner.Item.ValidFrom ?? (carry ? loser.Item.ValidFrom : null),
                    // A date and its precision travel together: from whichever side the date came.
                    ValidFromPrecision = winner.Item.ValidFrom is not null ? winner.Item.ValidFromPrecision
                        : carry ? loser.Item.ValidFromPrecision : DatePrecision.Unspecified,
                    ValidUntil = winner.Item.ValidUntil ?? (carry ? loser.Item.ValidUntil : null),
                    ValidUntilPrecision = winner.Item.ValidUntil is not null ? winner.Item.ValidUntilPrecision
                        : carry ? loser.Item.ValidUntilPrecision : DatePrecision.Unspecified,
                    OccurredOn = winner.Item.OccurredOn ?? (carry ? loser.Item.OccurredOn : null),
                    OccurredOnPrecision = winner.Item.OccurredOn is not null ? winner.Item.OccurredOnPrecision
                        : carry ? loser.Item.OccurredOnPrecision : DatePrecision.Unspecified,
                    SourceTurn = winner.Item.SourceTurn ?? (carry ? loser.Item.SourceTurn : null),
                    Replaces = winner.Item.Replaces ?? (carry ? loser.Item.Replaces : null),
                    // A stored memory the writer named travels with its kind of closing, from whichever side named it.
                    ReplacesId = winner.Item.ReplacesId ?? (carry ? loser.Item.ReplacesId : null),
                    ReplacementIsCorrection = winner.Item.ReplacesId is not null ? winner.Item.ReplacementIsCorrection
                        : carry && loser.Item.ReplacementIsCorrection,
                },
            });
            merged.Add(new IngestionItemOutcome
            {
                Kind = MemoryItemKind.Fact,
                Stage = IngestionStage.Persistence,
                Status = IngestionItemStatus.Skipped,
                SourceKey = $"{loser.Item.Subject} {loser.Item.Predicate} {loser.Item.Object}",
                ErrorCode = MemoryErrorCodes.FactMergedWithinExtraction,
                Effect = MemoryWriteEffect.MergedWithinExtraction,
                ErrorMessage = $"Same statement as '{winner.Item.Subject} {winner.Item.Predicate} {winner.Item.Object}' in this extraction.",
            });
        }
        if (merged.Count > 0)
            System.Diagnostics.Activity.Current?.SetTag("memory.persist.facts_merged", merged.Count);
        return (kept, merged);

        // The user's own words over the assistant's paraphrase of them (what memory records is what the
        // user said), then confidence; the earlier phrasing on a tie.
        static bool Prefer(ExtractedFact candidate, ExtractedFact current)
        {
            if (IsAssistant(candidate) != IsAssistant(current)) return IsAssistant(current);
            return candidate.Confidence > current.Confidence;
        }

        static bool IsAssistant(ExtractedFact fact) =>
            string.Equals(fact.SourceRole, "assistant", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Words that carry no content of their own when one object is compared with another.</summary>
    private static readonly HashSet<string> FunctionWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "my", "our", "his", "her", "their", "in", "on", "at", "of", "to", "for", "with", "by", "from", "and",
    };

    private static readonly HashSet<string> NegationWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "not", "no", "never", "none", "nothing", "nobody", "neither", "nor", "without", "cannot", "non",
    };

    /// <summary>
    /// Whether two facts CAN be one statement phrased twice, whatever their similarity: the same subject,
    /// only one of predicate and object worded differently, the same numbers, the same negations, and no
    /// conflicting validity window. Conservative on purpose: a missed merge leaves a duplicate line, a wrong
    /// one loses a fact.
    /// </summary>
    internal static bool MayBeOneStatement(ExtractedFact left, ExtractedFact right)
    {
        static string Key(string value) => MemoryTripleCanonicalizer.CanonicalValue(value);
        // The fact that names the user is how the name is found in every later session: never merged away.
        if (MemoryUserNames.IsNamingFact(left) || MemoryUserNames.IsNamingFact(right)) return false;
        if (Key(left.Subject) != Key(right.Subject)) return false;
        if (Key(left.Predicate) != Key(right.Predicate) && Key(left.Object) != Key(right.Object)) return false;
        // 38.6 (F15, show 04 run 11). Under one predicate, two objects that each say something the other does not
        // ("full marathon in May 2027" / "half marathon in April 2027") are two statements, however similar their
        // vectors: merged, the full marathon took the half marathon's end date and stopped being live a day later.
        // One object must be the other's fuller phrasing ("half marathon" / "half marathon in April").
        if (Key(left.Predicate) == Key(right.Predicate) && Key(left.Object) != Key(right.Object) &&
            !OneHoldsTheOther(left.Object, right.Object)) return false;
        if (Numbers(left) != Numbers(right) || Negations(left) != Negations(right)) return false;
        return Agree(left.ValidFrom, right.ValidFrom) && Agree(left.ValidUntil, right.ValidUntil) &&
               Agree(left.OccurredOn, right.OccurredOn);

        static bool Agree(DateTimeOffset? a, DateTimeOffset? b) => a is null || b is null || a == b;

        static bool OneHoldsTheOther(string a, string b)
        {
            var left = ContentWords(a);
            var right = ContentWords(b);
            return left.IsSubsetOf(right) || right.IsSubsetOf(left);
        }

        static HashSet<string> ContentWords(string value) =>
            Key(value).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(word => !FunctionWords.Contains(word))
                .ToHashSet(StringComparer.Ordinal);

        static string Numbers(ExtractedFact fact) => string.Join(
            ",", System.Text.RegularExpressions.Regex.Matches($"{fact.Predicate} {fact.Object}", "[0-9]+").Select(m => m.Value));

        static string Negations(ExtractedFact fact) => string.Join(
            ",", System.Text.RegularExpressions.Regex.Split(
                    $"{fact.Predicate} {fact.Object}".Replace("n't", " not", StringComparison.OrdinalIgnoreCase), "[^A-Za-z]+")
                .Where(NegationWords.Contains)
                .Select(word => word.ToLowerInvariant())
                .Order(StringComparer.Ordinal));
    }
}
