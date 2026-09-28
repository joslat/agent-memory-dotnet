using System.Text.RegularExpressions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Core.Memory;

namespace AgentMemory.Core.Extraction;

/// <summary>
/// 36.4. What a correction the extractor marked (<see cref="ExtractedFact.Replaces"/>,
/// <see cref="ExtractedPreference.Replaces"/>) closes: the live memory that stated the replaced value.
/// </summary>
/// <remarks>
/// <para>
/// Supersession by cardinality needs a single-valued relation, and many changes of mind have none: a preference
/// ("Arcade Fire, not Radiohead"), a plan ("the full marathon instead of the half"). The conversation says what is
/// replaced; the extractor writes it down (<c>"replaces": "Radiohead"</c>); this decides which stored memory that
/// names, conservatively, because closing the wrong one hides a true memory.
/// </para>
/// <list type="bullet">
/// <item>A <b>fact</b> of the same subject <i>mentions</i> the value when one of its object and the value contains the
/// other as whole words ("half marathon" for "the half marathon in April", and the other way round). Among the live
/// facts that mention it, those stating the winner's relation are closed. If none does, the one fact that mentions it
/// is closed, and only if it is the only one: "works for a wind energy firm, replaces the shipping company" closes
/// "works at a shipping company" and leaves "left the shipping company" (a true event) alone, while "is running the
/// full marathon, replaces the half marathon" closes the one "is training for half marathon".</item>
/// <item>A <b>preference</b> of the same category is closed when its text contains the value as whole words.</item>
/// </list>
/// </remarks>
internal static class Corrections
{
    /// <summary>The live facts, among <paramref name="candidates"/>, that a correction by <paramref name="winner"/> closes.</summary>
    /// <param name="candidates">The subject's facts.</param>
    /// <param name="winner">The correction.</param>
    /// <param name="replaced">The value it replaces, as said.</param>
    /// <param name="statedNow">
    /// Facts of the same extraction as the correction. Only the same relation's closes one of them: under another
    /// relation it is something else said now ("Oslo now, not Copenhagen; Copenhagen is still my favourite city"),
    /// and the fallback exists for what was stored before under another phrasing.
    /// </param>
    internal static IReadOnlyList<Fact> Closed(
        IEnumerable<Fact> candidates, Fact winner, string replaced, IReadOnlySet<string>? statedNow = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(winner);
        var value = Value(replaced);
        if (value.Length == 0) return [];
        var mentioning = candidates
            .Where(candidate => candidate.InvalidatedAtUtc is null && candidate.FactId != winner.FactId)
            .Where(candidate => Mentions(candidate.Object, value))
            .ToList();
        var relationKeys = MemoryRelationCardinality.ReplacedKeys(winner.Predicate);
        var sameRelation = mentioning
            .Where(candidate => relationKeys.Contains(MemoryTripleCanonicalizer.Canonical(candidate.Predicate), StringComparer.Ordinal))
            .ToList();
        if (sameRelation.Count > 0) return sameRelation;
        // Another relation only when the mention is unambiguous and not a coincidence of one word: the object IS the
        // value, or the two share at least two words ("half marathon" for "the half marathon in April"), never "6 kg"
        // for "6" nor "London" for "Google in London".
        mentioning = [.. mentioning.Where(candidate => statedNow?.Contains(candidate.FactId) != true)];
        return mentioning.Count == 1 &&
               (Value(mentioning[0].Object) == value || Math.Min(WordCount(mentioning[0].Object), WordCount(value)) >= 2)
            ? mentioning
            : [];
    }

    private static int WordCount(string text) =>
        Value(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Whether one of <paramref name="text"/> and <paramref name="value"/> contains the other as whole words.</summary>
    private static bool Mentions(string text, string value)
    {
        var canonical = Value(text);
        return canonical.Length > 0 && (ContainsWords(canonical, value) || ContainsWords(value, canonical));
    }

    /// <summary>
    /// 36.4. The single-valued relation a preference states, when it states one: "Favourite band is Arcade Fire" states
    /// <c>favourite band</c>, the same relation the <c>favourite</c> prefix makes single-valued for facts. Null otherwise.
    /// </summary>
    internal static string? SingleValuedRelation(string preferenceText)
    {
        var text = MemoryTripleCanonicalizer.CanonicalValue(preferenceText);
        var at = text.IndexOf(" is ", StringComparison.Ordinal);
        return at <= 0 ? null : MemoryRelationCardinality.Relation(text[..at]);
    }

    /// <summary>The value a preference stating a single-valued relation states ("Favourite band is Radiohead": radiohead).</summary>
    internal static string? StatedValue(string preferenceText)
    {
        var text = MemoryTripleCanonicalizer.CanonicalValue(preferenceText);
        var at = text.IndexOf(" is ", StringComparison.Ordinal);
        return at <= 0 || SingleValuedRelation(preferenceText) is null ? null : Value(text[(at + 4)..]);
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> states another value of the single-valued relation <paramref name="winner"/>
    /// states: "Favourite band is Radiohead" for "Favourite band is Arcade Fire". Found in simulated conversations: the
    /// model did not always mark "not Radiohead" as a correction, and both favourites stayed live.
    /// </summary>
    internal static bool Replaces(Preference winner, Preference candidate)
    {
        ArgumentNullException.ThrowIfNull(winner);
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.InvalidatedAtUtc is not null || candidate.PreferenceId == winner.PreferenceId) return false;
        var relation = SingleValuedRelation(winner.PreferenceText);
        return relation is not null &&
               string.Equals(SingleValuedRelation(candidate.PreferenceText), relation, StringComparison.Ordinal) &&
               !string.Equals(Value(candidate.PreferenceText), Value(winner.PreferenceText), StringComparison.Ordinal);
    }

    internal static bool Closes(Preference candidate, Preference winner, string replaced)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(winner);
        if (candidate.InvalidatedAtUtc is not null || candidate.PreferenceId == winner.PreferenceId) return false;
        return Names(candidate.PreferenceText, replaced);
    }

    /// <summary>Whether <paramref name="text"/> names the replaced value as whole words.</summary>
    internal static bool Names(string text, string replaced)
    {
        var value = Value(replaced);
        return value.Length > 0 && ContainsWords(text, value);
    }

    /// <summary>The replaced value as compared: canonical, without a leading article or possessive.</summary>
    internal static string Value(string? replaced)
    {
        var value = MemoryTripleCanonicalizer.CanonicalValue(replaced);
        foreach (var lead in new[] { "the ", "a ", "an ", "my ", "our " })
        {
            if (value.StartsWith(lead, StringComparison.Ordinal) && value.Length > lead.Length)
                return value[lead.Length..];
        }
        return value;
    }

    private static bool ContainsWords(string text, string value) =>
        Regex.IsMatch(
            MemoryTripleCanonicalizer.CanonicalValue(text),
            @"(?<![\p{L}\p{N}])" + Regex.Escape(value) + @"(?![\p{L}\p{N}])",
            RegexOptions.CultureInvariant);
}
