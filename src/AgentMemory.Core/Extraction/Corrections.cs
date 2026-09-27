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
/// <item>A <b>fact</b> of the same subject is closed when its object <i>is</i> the replaced value, or when it states
/// the same relation and its object <i>contains</i> the value as whole words ("half marathon in April" for
/// "the half"). A different relation that merely mentions the value ("weighs 6 kg" for "6") is left alone.</item>
/// <item>A <b>preference</b> of the same category is closed when its text contains the value as whole words.</item>
/// </list>
/// </remarks>
internal static class Corrections
{
    internal static bool Closes(Fact candidate, Fact winner, string replaced)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(winner);
        if (candidate.InvalidatedAtUtc is not null || candidate.FactId == winner.FactId) return false;
        var value = Value(replaced);
        if (value.Length == 0) return false;
        if (string.Equals(MemoryTripleCanonicalizer.CanonicalValue(candidate.Object), value, StringComparison.Ordinal))
            return true;
        var sameRelation = MemoryRelationCardinality.ReplacedKeys(winner.Predicate)
            .Contains(MemoryTripleCanonicalizer.Canonical(candidate.Predicate), StringComparer.Ordinal);
        return sameRelation && ContainsWords(candidate.Object, value);
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
