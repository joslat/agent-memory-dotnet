namespace AgentMemory.AgentFramework.Recall;

/// <summary>
/// Whether a turn's user text is nothing but greetings and acknowledgements.
/// </summary>
/// <remarks>
/// <para>
/// Extracted so <see cref="HeuristicAutomaticRecallPolicy"/> and <see cref="TrivialTurnRecallPolicy"/>
/// share one definition. Two policies disagreeing about what "trivial" means would be a silent
/// behaviour difference between an opt-in policy and the default one.
/// </para>
/// <para>
/// <b>Deliberately not regex-based.</b> An earlier version used a single repeating-group pattern
/// (<c>^(\s*(hi|...)\s*)+$</c>) that exhibited catastrophic backtracking — verified experimentally, a
/// string of ~30 repeated short fragments blocked the calling thread for multiple seconds. Plain
/// tokenization is linear and has no such failure mode.
/// </para>
/// </remarks>
internal static class TrivialTurnDetector
{
    private static readonly HashSet<string> GreetingPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        "hi", "hello", "hey", "thanks", "thank you", "ok", "okay", "sure", "got it",
        "yes", "no", "yep", "nope", "sounds good", "great", "cool", "noted", "understood"
    };

    private static readonly char[] WordSeparators = { ' ', '\t', '\r', '\n', '.', ',', '!', '?' };

    /// <summary>
    /// True when <paramref name="text"/> is empty, whitespace, or composed entirely of greeting and
    /// acknowledgement words/phrases — e.g. "hi", "ok, got it.", "sounds good".
    /// </summary>
    internal static bool IsGreetingOrAcknowledgementOnly(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        var words = text.ToLowerInvariant().Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return true;

        var i = 0;
        while (i < words.Length)
        {
            if (i + 1 < words.Length && GreetingPhrases.Contains($"{words[i]} {words[i + 1]}"))
            {
                i += 2;
                continue;
            }

            if (!GreetingPhrases.Contains(words[i]))
                return false;

            i++;
        }

        return true;
    }
}
