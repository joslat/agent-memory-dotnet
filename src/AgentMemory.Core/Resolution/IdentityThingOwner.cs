using System.Text.RegularExpressions;
using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Resolution;

/// <summary>
/// ER-THING-OWNER-GUARD (IBP-2's namesake guard): whether the turn being decided gives a place or thing to another named
/// person than the candidate's name does. The candidate's name has a possessor ("user's tinny", "Ama's parents' shack");
/// the turn says its head word as "X's H" or "X ... his/her/their H" within one sentence, with up to two words before H
/// and X a capitalised word that is neither a function word nor a word of the candidate's names. The counterpart for
/// places and things of the writer's apposition guard: a relation the turn states contradicts the stored one.
/// </summary>
internal static class IdentityThingOwner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);
    private const string Word = @"[\p{L}\p{M}][\p{L}\p{M}'’-]*";
    private static readonly Regex Words = new(Word, RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex Sentences = new(@"[^.!?;]+", RegexOptions.CultureInvariant, Timeout);

    private static readonly HashSet<string> Prepositions = new(
        ["in", "at", "on", "of", "near", "by", "for", "from", "with", "behind", "beside", "outside", "inside"], StringComparer.Ordinal);

    /// <summary>Capitalised words that are not a person who could own something ("Then his bicycle broke").</summary>
    private static readonly HashSet<string> NotOwners = new(
        ["The", "A", "An", "And", "But", "Or", "So", "Then", "When", "While", "After", "Before", "Because", "If", "Since",
         "Though", "Although", "Once", "Now", "Today", "Yesterday", "Tomorrow", "Tonight", "This", "That", "These", "Those",
         "There", "Here", "We", "I", "You", "He", "She", "It", "They", "Our", "My", "Your", "His", "Her", "Their", "Its", "On",
         "In", "At", "For", "To", "Of", "By", "With", "From", "Last", "Next", "Every", "Each", "Also", "Even", "Just", "Still",
         "Later", "Yes", "No", "Oh", "Well", "Correction", "Sorry"], StringComparer.Ordinal);

    public static bool IsPlaceOrThing(string? type) => type is "LOCATION" or "OBJECT";

    public static bool Contradicts(Entity candidate, IEnumerable<string> turns)
    {
        try
        {
            var name = candidate.Name ?? "";
            var nameWords = Words.Matches(name).Select(m => m.Value).ToArray();
            if (!nameWords.Any(IsPossessive)) return false;
            var head = Head(nameWords);
            if (head.Length == 0) return false;
            var known = new[] { name, candidate.CanonicalName ?? "" }.Concat(candidate.Aliases)
                .SelectMany(n => Words.Matches(n).Select(m => Bare(m.Value)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var h = Regex.Escape(head);
            var possessed = new Regex($@"(?<![\p{{L}}\p{{M}}])(?<x>\p{{Lu}}[\p{{L}}\p{{M}}-]*)['’]s\s+(?:{Word}\s+){{0,2}}(?i:{h})(?![\p{{L}}\p{{M}}])",
                RegexOptions.CultureInvariant, Timeout);
            var pronoun = new Regex($@"(?<![\p{{L}}\p{{M}}])(?i:his|her|their)\s+(?:{Word}\s+){{0,2}}(?i:{h})(?![\p{{L}}\p{{M}}])",
                RegexOptions.CultureInvariant, Timeout);
            foreach (var turn in turns)
            {
                foreach (Match sentence in Sentences.Matches(turn ?? ""))
                {
                    var text = sentence.Value;
                    foreach (Match match in possessed.Matches(text))
                        if (IsOtherOwner(match.Groups["x"].Value, known)) return true;
                    foreach (Match match in pronoun.Matches(text))
                    {
                        var before = Words.Matches(text[..match.Index]).Select(m => m.Value).Reverse().Take(8);
                        var owner = before.FirstOrDefault(w => char.IsUpper(w[0]) && !NotOwners.Contains(w));
                        if (owner is not null && IsOtherOwner(owner, known)) return true;
                    }
                }
            }
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return true; // unread is not evidence of the same owner: the "same" stays for review
        }
    }

    private static bool IsOtherOwner(string word, IReadOnlySet<string> known) =>
        word.Length > 1 && !NotOwners.Contains(word) && !known.Contains(Bare(word));

    private static bool IsPossessive(string word) =>
        word.EndsWith("'s", StringComparison.OrdinalIgnoreCase) || word.EndsWith("’s", StringComparison.OrdinalIgnoreCase) ||
        word.EndsWith("s'", StringComparison.OrdinalIgnoreCase) || word.EndsWith("s’", StringComparison.OrdinalIgnoreCase);

    private static string Bare(string word) =>
        word.EndsWith("'s", StringComparison.OrdinalIgnoreCase) || word.EndsWith("’s", StringComparison.OrdinalIgnoreCase) ? word[..^2]
        : word.EndsWith('\'') || word.EndsWith('’') ? word[..^1] : word;

    /// <summary>The name's head word: the word before its first preposition ("shack" in "Ama's shack at the beach"), else its last.</summary>
    private static string Head(IReadOnlyList<string> words)
    {
        for (var i = 1; i < words.Count; i++)
            if (Prepositions.Contains(words[i].ToLowerInvariant())) return Bare(words[i - 1]);
        return words.Count == 0 ? "" : Bare(words[^1]);
    }
}
