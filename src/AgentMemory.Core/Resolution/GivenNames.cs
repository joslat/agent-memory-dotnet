using System.Globalization;
using System.Text;
using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Resolution;

/// <summary>
/// 40.93. Two people who share part of a name but differ in another are two people, however alike their full names look
/// to a fuzzy or semantic matcher: "Erik Halvorsen" and "Sven Halvorsen" share a surname (name embeddings score them 0.88),
/// and resolving one onto the other erased the brother and pointed his relationships at the owner. A different order
/// ("Smith John"), an initial ("S. Halvorsen"), a short form ("Carl M."), an honorific ("Dr.") or a one-letter typo still
/// resolve; exact names and aliases are never affected.
/// </summary>
internal static class GivenNames
{
    private static readonly HashSet<string> Honorifics = new(StringComparer.Ordinal)
    {
        "dr", "mr", "mrs", "ms", "miss", "mx", "prof", "sir", "dame", "lord", "lady", "fr", "rev", "sr", "sra", "srta", "don", "doña",
        "herr", "frau", "monsieur", "madame", "mme", "mlle", "signor", "signora",
    };

    /// <summary>Whether <paramref name="type"/> names a person (PERSON, Person, …).</summary>
    public static bool IsPerson(string? type) => string.Equals(type, "person", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="candidate"/> is a full name (two words or more) and every full name the existing entity
    /// goes by (its name and aliases of two words or more) is another person's: they share a word, and each keeps a word
    /// the other cannot account for.
    /// </summary>
    public static bool Differ(string candidate, Entity existing)
    {
        var mine = Words(candidate);
        if (mine.Count < 2) return false;
        var told = false;
        foreach (var name in existing.Aliases.Prepend(existing.Name).Append(existing.CanonicalName ?? ""))
        {
            var theirs = Words(name);
            if (theirs.Count < 2) continue;
            told = true;
            if (!Distinct(mine, theirs)) return false;
        }
        return told;
    }

    private static bool Distinct(List<string> a, List<string> b)
    {
        if (!a.Intersect(b).Any()) return false;   // nothing shared: leave it to the matcher
        var onlyA = a.Except(b).ToList();
        var onlyB = b.Except(a).ToList();
        foreach (var x in onlyA.ToList())
        {
            var y = onlyB.FirstOrDefault(y => Compatible(x, y));
            if (y is null) continue;
            onlyA.Remove(x);
            onlyB.Remove(y);
        }
        return onlyA.Count > 0 && onlyB.Count > 0;
    }

    /// <summary>An initial, a short form (a prefix of three letters or more), or a one-letter typo of a long word.</summary>
    private static bool Compatible(string x, string y) =>
        (x.Length == 1 && y.StartsWith(x, StringComparison.Ordinal)) || (y.Length == 1 && x.StartsWith(y, StringComparison.Ordinal))
        || (Math.Min(x.Length, y.Length) >= 3 && (x.StartsWith(y, StringComparison.Ordinal) || y.StartsWith(x, StringComparison.Ordinal)))
        || (Math.Min(x.Length, y.Length) >= 5 && OneEdit(x, y));

    private static bool OneEdit(string x, string y)
    {
        if (Math.Abs(x.Length - y.Length) > 1) return false;
        int i = 0, j = 0, edits = 0;
        while (i < x.Length && j < y.Length)
        {
            if (x[i] == y[j]) { i++; j++; continue; }
            if (++edits > 1) return false;
            if (x.Length > y.Length) i++;
            else if (y.Length > x.Length) j++;
            else { i++; j++; }
        }
        return edits + (x.Length - i) + (y.Length - j) <= 1;
    }

    /// <summary>The name's words, folded (lower case, no accents, no trailing dot), without honorifics.</summary>
    private static List<string> Words(string? name) =>
        [.. (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(Fold).Where(t => t.Length > 0 && !Honorifics.Contains(t))];

    private static string Fold(string token)
    {
        var lower = token.Trim().TrimEnd('.', ',').ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(lower.Length);
        foreach (var c in lower)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString();
    }
}
