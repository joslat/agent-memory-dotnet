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
    internal static IReadOnlyList<Fact> Closed(IEnumerable<Fact> candidates, Fact winner, string replaced)
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
        return mentioning.Count == 1 ? mentioning : [];
    }

    /// <summary>Whether one of <paramref name="text"/> and <paramref name="value"/> contains the other as whole words.</summary>
    private static bool Mentions(string text, string value)
    {
        var canonical = Value(text);
        return canonical.Length > 0 && (ContainsWords(canonical, value) || ContainsWords(value, canonical));
    }

    internal static bool Closes(Preference candidate, Preference winner, string replaced)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(winner);
        if (candidate.InvalidatedAtUtc is not null || candidate.PreferenceId == winner.PreferenceId) return false;
        var value = Value(replaced);
        return value.Length > 0 && ContainsWords(candidate.PreferenceText, value);
    }

    /// <summary>The replaced value as compared: canonical, without a leading article or possessive.</summary>
    private static string Value(string? replaced)
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
