using System.Globalization;
using System.Text;

namespace AgentMemory.Core.Memory;

/// <summary>
/// ILC-33: which confirmed people a question names. A name matches as whole words, case and accents aside. A name two
/// people share names neither of them, so recall never guesses between homonyms; a name only one of them has (an alias)
/// still names that one. An unconfirmed person (an open proposal) is never named, but its name still makes a namesake
/// ambiguous. One-letter names and pronouns name nobody.
/// </summary>
internal static class PersonMentions
{
    /// <summary>
    /// People read per question. An owner with this many people or more names nobody (a namesake may lie past the bound),
    /// so the reads stop there; below it they grow with the owner's people and their bindings (REV4-1).
    /// </summary>
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

    internal sealed record Person(string Id, IReadOnlyList<string> Surfaces, bool Confirmed = true);

    /// <summary>
    /// REV1-L11: single-word names that are also everyday words. One counts only written as a name: capitalised and not
    /// the first word of a sentence ("I met Will", not "Will it rain?").
    /// </summary>
    private static readonly HashSet<string> CommonWords = new(StringComparer.Ordinal)
    {
        "will", "may", "june", "april", "august", "mark", "bill", "art", "grace", "hope", "joy", "faith", "rose", "lily", "ray",
        "sky", "summer", "dawn", "eve", "holly", "ivy", "jack", "frank", "rich", "guy", "don", "sue", "pat", "rob", "sunny",
        "page", "chase", "chance", "cliff", "dean", "drew", "hunter", "iris", "jade", "king", "lane", "miles", "penny", "pearl",
        "ruby", "sandy", "storm", "wade", "brook", "crystal", "earl", "glen", "hazel", "heather", "honey", "autumn", "winter",
        "river", "rain", "star", "violet", "amber", "basil", "forest", "major", "price", "young", "white", "black", "brown",
        "green", "paz", "luz", "sol", "rosa", "mar", "clara", "blanca", "cruz", "pilar", "rocio", "alba", "angel", "flor",
        "rey", "leon", "lucero", "estrella", "paloma", "fe", "esperanza", "consuelo", "amparo", "soledad", "dolores",
    };

    /// <summary>
    /// REV3-2: the spellings of <paramref name="personId"/> that no other of these people has, by the same whole-word key
    /// <see cref="Named"/> uses. A spelling two people share is neither's in the hand-over to current values.
    /// </summary>
    internal static IReadOnlyList<string> OwnSpellings(IReadOnlyList<Person> people, string personId)
    {
        var holders = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var person in people)
            foreach (var surface in person.Surfaces)
                if (Key(surface) is { } key)
                {
                    if (!holders.TryGetValue(key, out var ids)) holders[key] = ids = new HashSet<string>(StringComparer.Ordinal);
                    ids.Add(person.Id);
                }
        return [.. people.Where(person => person.Id == personId).SelectMany(person => person.Surfaces)
            .Where(surface => Key(surface) is { } key && holders[key].Count == 1)];
    }

    /// <summary>Titles that may stand before a name without making it part of a longer one.</summary>
    private static readonly HashSet<string> Titles = new(StringComparer.Ordinal)
    {
        "mr", "mrs", "ms", "miss", "dr", "prof", "sir", "dame", "sr", "sra", "srta", "don", "dona", "lic", "ing",
    };

    /// <summary>REV2-2: how many spellings the light first read of people takes; a full page goes on to the full read.</summary>
    internal const int MaxSpellingsRead = 10_000;

    /// <summary>
    /// REV2-2: whether any of these spellings is in the question at all, as whole words, case and accents aside. A
    /// necessary condition for <see cref="Named"/> to name anyone; when it is false recall reads nothing more.
    /// </summary>
    internal static bool Mentions(string question, IReadOnlyList<string> spellings)
    {
        if (string.IsNullOrWhiteSpace(question) || spellings.Count == 0) return false;
        var words = Words(question).Take(MaxQuestionWords).ToArray();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var start = 0; start < words.Length; start++)
            for (var length = 1; length <= MaxNameWords && start + length <= words.Length; length++)
                keys.Add(string.Join(' ', words, start, length));
        return spellings.Any(surface => Key(surface) is { } key && keys.Contains(key));
    }

    /// <summary>The ids of the people the question names, in the order it names them, at most <see cref="MaxNamed"/>.</summary>
    internal static IReadOnlyList<string> Named(string question, IReadOnlyList<Person> people)
    {
        if (string.IsNullOrWhiteSpace(question) || people.Count == 0) return [];
        var tokens = Tokens(question).Take(MaxQuestionWords).ToArray();
        var words = tokens.Select(token => token.Word).ToArray();
        var starts = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var start = 0; start < words.Length; start++)
            for (var length = 1; length <= MaxNameWords && start + length <= words.Length; length++)
            {
                var key = string.Join(' ', words, start, length);
                if (!starts.TryGetValue(key, out var at)) starts[key] = at = [];
                at.Add(start);
            }

        // Who each name in the question could be; a name more than one person has is left out below.
        var owners = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var person in people)
            foreach (var surface in person.Surfaces)
            {
                var key = Key(surface);
                if (key is null || !starts.ContainsKey(key)) continue;
                if (!owners.TryGetValue(key, out var ids)) owners[key] = ids = new HashSet<string>(StringComparer.Ordinal);
                ids.Add(person.Id);
            }
        static int Length(string key) => key.Count(c => c == ' ') + 1;
        bool Written(string key) => Length(key) > 1 || !CommonWords.Contains(key) ||
            starts[key].Any(start => tokens[start].Capitalized && !tokens[start].SentenceStart);
        var said = owners.Keys.Where(Written).ToList();
        // REV1-M3: a name inside a longer name the question says ("Ana" in "Ana García") is part of that name, not a name
        // of its own: only the places where it stands alone count.
        // REV2-6: and a name followed or preceded by another capitalised word ("Ana Torres") is part of a longer name the
        // store does not know, not this person (titles such as "Dr" aside).
        // Only a word joined to the name by a space counts: "Diego, Tomás" are two people.
        bool Bordered(int start, int length) =>
            tokens[start].Joined && Capitalised(start - 1) && !Titles.Contains(tokens[start - 1].Word) ||
            start + length < tokens.Length && tokens[start + length].Joined && Capitalised(start + length);
        bool Capitalised(int at) => at >= 0 && at < tokens.Length && tokens[at].Capitalized && !tokens[at].SentenceStart && tokens[at].Word != "i";
        IEnumerable<int> Alone(string key) => starts[key].Where(start => !Bordered(start, Length(key)) && !said.Any(other => Length(other) > Length(key) &&
            starts[other].Any(at => at <= start && start + Length(key) <= at + Length(other))));
        var confirmed = people.Where(person => person.Confirmed).Select(person => person.Id).ToHashSet(StringComparer.Ordinal);
        return said.Select(key => (Key: key, Alone: Alone(key).ToList()))
            .Where(name => name.Alone.Count > 0 && owners[name.Key].Count == 1 && confirmed.Contains(owners[name.Key].Single()))
            .Select(name => (Id: owners[name.Key].Single(), Position: name.Alone.Min()))
            .GroupBy(named => named.Id, StringComparer.Ordinal)
            .Select(group => (Id: group.Key, Position: group.Min(named => named.Position)))
            .OrderBy(named => named.Position).ThenBy(named => named.Id, StringComparer.Ordinal)
            .Take(MaxNamed).Select(named => named.Id).ToArray();
    }

    /// <summary>
    /// The question's words as <see cref="Words"/> makes them, with how each was written and whether only spaces (or an
    /// apostrophe or hyphen) separate it from the word before.
    /// </summary>
    private static IEnumerable<(string Word, bool Capitalized, bool SentenceStart, bool Joined)> Tokens(string text)
    {
        var word = new StringBuilder();
        bool capitalized = false, sentenceStart = true, nextStartsSentence = true, joined = false, nextJoined = false;
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c))
            {
                if (word.Length == 0) { capitalized = char.IsUpper(c); sentenceStart = nextStartsSentence; joined = nextJoined; }
                word.Append(char.ToLowerInvariant(c));
                continue;
            }
            if (word.Length > 0)
            {
                yield return (word.ToString(), capitalized, sentenceStart, joined);
                word.Clear(); nextStartsSentence = false; nextJoined = true;
            }
            if (c is '.' or '!' or '?' or '¿' or '¡' or '\n') nextStartsSentence = true;
            if (!char.IsWhiteSpace(c) && c is not ('\'' or '\u2019' or '-')) nextJoined = false;
        }
        if (word.Length > 0) yield return (word.ToString(), capitalized, sentenceStart, joined);
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
