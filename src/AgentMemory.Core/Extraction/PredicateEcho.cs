using System.Text.RegularExpressions;
using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Extraction;

/// <summary>
/// 37.2. A predicate that ends with its own object: the model wrote the object twice ("Daniel | is a chef | chef",
/// "Bill | is to go down the chimney | the chimney"), and every renderer printed it twice ("Daniel is a chef chef").
/// The object's words are trimmed from the end of the predicate, once, at write, so the stored triple reads as said.
/// </summary>
/// <remarks>
/// Only when something is left of the predicate, and only a whole-word ending: "works at | Acme" and "lives in | Oslo"
/// are unchanged, and "is | chef" is not emptied. The object is matched with and without a leading article ("the
/// chimney" and "chimney"), because the model repeats it either way.
/// </remarks>
internal static partial class PredicateEcho
{
    [GeneratedRegex(@"[\s_]+", RegexOptions.CultureInvariant)]
    private static partial Regex Separators();

    private static readonly string[] Articles = ["a", "an", "the"];

    internal static ExtractedFact Trim(ExtractedFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        var predicate = Words(fact.Predicate);
        var @object = Words(fact.Object);
        if (predicate.Length < 2 || @object.Length == 0) return fact;

        foreach (var echo in Forms(@object))
        {
            if (echo.Length >= predicate.Length || !EndsWith(predicate, echo)) continue;
            var kept = predicate[..^echo.Length];
            // An article left dangling belongs to the object: "lives in the | Netherlands" is "lives in | the
            // Netherlands", "is a chef | chef" is "is | a chef" (review round 1: "lives in the" named no relation).
            if (kept.Length > 1 && Articles.Contains(kept[^1], StringComparer.OrdinalIgnoreCase) &&
                !Articles.Contains(@object[0], StringComparer.OrdinalIgnoreCase))
                return fact with { Predicate = string.Join(' ', kept[..^1]), Object = kept[^1] + " " + fact.Object.Trim() };
            return fact with { Predicate = string.Join(' ', kept) };
        }
        return fact;
    }

    /// <summary>The object's words as the predicate may end with them: whole, then without a leading article.</summary>
    private static IEnumerable<string[]> Forms(string[] @object)
    {
        yield return @object;
        if (@object.Length > 1 && Articles.Contains(@object[0], StringComparer.OrdinalIgnoreCase))
            yield return @object[1..];
    }

    private static bool EndsWith(string[] words, string[] tail)
    {
        for (var i = 1; i <= tail.Length; i++)
        {
            if (!string.Equals(words[^i], tail[^i], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static string[] Words(string? text) =>
        string.IsNullOrWhiteSpace(text) ? [] : Separators().Split(text.Trim()).Where(w => w.Length > 0).ToArray();
}
