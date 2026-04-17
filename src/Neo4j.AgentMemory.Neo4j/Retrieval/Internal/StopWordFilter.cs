using System.Text.RegularExpressions;

namespace Neo4j.AgentMemory.Neo4j.Retrieval.Internal;

/// <summary>
/// Common English stop words for fulltext search query filtering.
/// </summary>
internal static partial class StopWordFilter
{
    private static readonly HashSet<string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        // Question words
        "what", "where", "who", "when", "why", "how", "which", "whose",
        // Common verbs
        "is", "are", "was", "were", "be", "been", "being",
        "have", "has", "had", "do", "does", "did",
        "will", "would", "could", "should", "can", "may", "might", "must",
        // Articles and prepositions
        "a", "an", "the", "of", "in", "on", "at", "to", "for",
        "with", "by", "from", "as", "into", "about",
        // Pronouns
        "i", "me", "my", "we", "us", "our",
        "you", "your", "it", "its", "they", "them", "their",
        // Conjunctions
        "and", "or", "but", "if", "then", "so",
        "that", "this", "these", "those",
        // Common words in questions
        "tell", "show", "find", "give", "get", "list",
        "describe", "explain", "any", "all", "some",
        "most", "more", "less", "involve", "related",
        "regarding", "concerning"
    };

    internal static string ExtractKeywords(string text)
    {
        var words = WordPattern().Matches(text.ToLowerInvariant());
        var keywords = words
            .Select(m => m.Value)
            .Where(w => w.Length > 1 && !Words.Contains(w));
        return string.Join(" ", keywords);
    }

    [GeneratedRegex(@"\b\w+\b")]
    private static partial Regex WordPattern();
}
