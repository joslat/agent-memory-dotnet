using System.Globalization;
using System.Text;

namespace AgentMemory.Core.Memory;

/// <summary>
/// ILC-33: which confirmed people a question names. A name matches as whole words, case and accents aside. A name two
/// people share names neither of them, so recall never guesses between homonyms; a name only one of them has (an alias)
/// still names that one. One-letter names and pronouns name nobody.
/// </summary>
internal static class PersonMentions
{
    /// <summary>People read per question; the read is bounded so a large owner costs the same.</summary>
    internal const int MaxPeopleRead = 1000;

    /// <summary>People expanded per question.</summary>
    internal const int MaxNamed = 3;

    private const int MaxQuestionWords = 512;
    private const int MaxNameWords = 6;

    private static readonly HashSet<string> Pronouns = new(StringComparer.Ordinal)
    {
        "i", "me", "my", "mine", "you", "your", "we", "us", "our", "he", "him", "his", "she", "her", "hers", "they", "them",
        "their", "it", "user", "assistant",
    };

    internal sealed record Person(string Id, IReadOnlyList<string> Surfaces);

    /// <summary>The ids of the people the question names, in the order it names them, at most <see cref="MaxNamed"/>.</summary>
    internal static IReadOnlyList<string> Named(string question, IReadOnlyList<Person> people)
    {
        if (string.IsNullOrWhiteSpace(question) || people.Count == 0) return [];
        var words = Words(question).Take(MaxQuestionWords).ToArray();
        var at = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var start = 0; start < words.Length; start++)
            for (var length = 1; length <= MaxNameWords && start + length <= words.Length; length++)
                at.TryAdd(string.Join(' ', words, start, length), start);

        // Who each name in the question could be; a name more than one person has is left out.
        var owners = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var person in people)
            foreach (var surface in person.Surfaces)
            {
                var key = Key(surface);
                if (key is null || !at.ContainsKey(key)) continue;
                if (!owners.TryGetValue(key, out var ids)) owners[key] = ids = new HashSet<string>(StringComparer.Ordinal);
                ids.Add(person.Id);
            }
        return owners.Where(pair => pair.Value.Count == 1)
            .Select(pair => (Id: pair.Value.Single(), Position: at[pair.Key]))
            .GroupBy(named => named.Id, StringComparer.Ordinal)
            .Select(group => (Id: group.Key, Position: group.Min(named => named.Position)))
            .OrderBy(named => named.Position).ThenBy(named => named.Id, StringComparer.Ordinal)
            .Take(MaxNamed).Select(named => named.Id).ToArray();
    }

    private static string? Key(string? surface)
    {
        if (string.IsNullOrWhiteSpace(surface)) return null;
        var words = Words(surface).ToArray();
        if (words.Length is 0 or > MaxNameWords) return null;
        var key = string.Join(' ', words);
        return key.Length < 2 || Pronouns.Contains(key) ? null : key;
    }

    /// <summary>Lower-case words without accents; anything that is not a letter or a digit separates them.</summary>
    private static IEnumerable<string> Words(string text)
    {
        var folded = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                folded.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        return folded.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}
