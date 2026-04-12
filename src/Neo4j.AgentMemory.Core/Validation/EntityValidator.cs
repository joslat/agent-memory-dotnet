using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Options;

namespace Neo4j.AgentMemory.Core.Validation;

/// <summary>
/// Static utility for validating extracted entities before persistence.
/// Ports the 221-stopword validation logic from the Python reference implementation.
/// </summary>
public static class EntityValidator
{
    // ~221 common English stopwords — case-insensitive comparison applied at call site
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "i", "me", "my", "myself", "we", "our", "ours", "ourselves",
        "you", "your", "yours", "yourself", "yourselves",
        "he", "him", "his", "himself",
        "she", "her", "hers", "herself",
        "it", "its", "itself",
        "they", "them", "their", "theirs", "themselves",
        "what", "which", "who", "whom",
        "this", "that", "these", "those",
        "am", "is", "are", "was", "were", "be", "been", "being",
        "have", "has", "had", "having",
        "do", "does", "did", "doing",
        "a", "an", "the",
        "and", "but", "if", "or", "because", "as", "until", "while",
        "of", "at", "by", "for", "with", "about", "against", "between",
        "into", "through", "during", "before", "after", "above", "below",
        "to", "from", "up", "down", "in", "out", "on", "off",
        "over", "under", "again", "further", "then", "once",
        "here", "there", "when", "where", "why", "how",
        "all", "any", "both", "each", "few", "more", "most",
        "other", "some", "such",
        "no", "nor", "not", "only", "own", "same", "so", "than", "too", "very",
        "s", "t", "can", "will", "just", "don", "should", "now",
        "d", "ll", "m", "o", "re", "ve", "y",
        "ain", "aren", "couldn", "didn", "doesn", "hadn", "hasn",
        "haven", "isn", "ma", "mightn", "mustn", "needn",
        "shan", "shouldn", "wasn", "weren", "won", "wouldn",
        "could", "would", "might", "must", "shall",
        "also", "already", "still", "even", "ever",
        "back", "well", "away", "often", "never", "always",
        "usually", "sometimes", "today", "tomorrow", "yesterday",
        "yet", "quite", "rather", "almost", "enough",
        "perhaps", "maybe", "either", "neither",
        "although", "though", "whereas",
        "however", "nevertheless", "therefore", "hence", "thus",
        "meanwhile", "besides", "moreover", "furthermore",
        "another", "many", "said", "say", "says", "new",
        "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "first", "second", "last", "next", "much", "per",
        "less", "least", "something", "anything", "nothing", "everything",
        "someone", "anyone", "nobody", "everybody", "everyone",
        "somewhere", "anywhere", "nowhere", "everywhere",
        "thing", "things", "way", "ways", "time", "times",
        "used", "using", "use", "make", "made", "let", "see", "know",
        "get", "got", "give", "go", "come", "came", "put",
        "take", "took", "want", "need", "look", "seem",
        "feel", "try", "ask", "keep", "run", "turn", "show",
        "like", "may", "each", "those", "between", "own",
        "same", "those", "after", "since", "while", "about",
        "against", "during", "without", "within", "along",
        "following", "across", "behind", "beyond", "plus",
        "except", "up", "out", "around", "down", "off", "above",
        "near", "been", "among", "throughout", "despite",
        "regarding", "toward", "towards", "whether"
    };

    /// <summary>
    /// Filters a list of extracted entities, removing those that fail validation rules.
    /// </summary>
    public static IReadOnlyList<ExtractedEntity> ValidateEntities(
        IReadOnlyList<ExtractedEntity> entities,
        EntityValidationOptions options)
    {
        var result = new List<ExtractedEntity>(entities.Count);
        foreach (var entity in entities)
        {
            if (IsValid(entity, options))
                result.Add(entity);
        }
        return result;
    }

    /// <summary>
    /// Returns true if the entity passes all configured validation rules.
    /// </summary>
    public static bool IsValid(ExtractedEntity entity, EntityValidationOptions options)
    {
        var name = entity.Name?.Trim();
        if (string.IsNullOrEmpty(name))
            return false;

        if (name.Length < options.MinNameLength)
            return false;

        if (options.RejectNumericOnly && IsNumericOnly(name))
            return false;

        if (options.RejectPunctuationOnly && IsPunctuationOnly(name))
            return false;

        if (options.UseStopwordFilter && Stopwords.Contains(name))
            return false;

        return true;
    }

    private static bool IsNumericOnly(string name)
    {
        bool hasDigit = false;
        foreach (var c in name)
        {
            if (char.IsDigit(c))
                hasDigit = true;
            else if (c != '.' && c != ',' && c != '-')
                return false;
        }
        return hasDigit;
    }

    private static bool IsPunctuationOnly(string name)
    {
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c))
                return false;
        }
        return true;
    }
}
