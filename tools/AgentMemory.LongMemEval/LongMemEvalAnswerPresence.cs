namespace AgentMemory.LongMemEval;

/// <summary>
/// Whether a question's gold answer appears in stored memory <b>at all</b>.
/// </summary>
/// <remarks>
/// Every other instrument in this track answers <i>"did retrieval find what was stored?"</i> —
/// relation completeness, gold coverage, recall@K. **None asked whether the answer was ever stored.**
/// The gap was expensive: question <c>32260d93</c> asks what was recommended, the assistant turn
/// holding the recommendation produced only facts about the <i>user</i>, and the resulting
/// <c>D = 0</c> was read as a missing-vocabulary problem when the content simply did not exist.
/// <para>
/// This gate separates an <b>extraction</b> failure from a <b>retrieval</b> failure. When the answer's
/// distinctive tokens appear nowhere in memory, the question is unanswerable from memory in
/// principle, and every retrieval metric computed on it is measuring noise — it can only ever fail,
/// and it fails for a reason retrieval metrics cannot name.
/// </para>
/// <para>
/// <b>Token overlap on purpose.</b> This is a floor, not a scorer. A cheap check that can only say
/// "definitely absent" is worth more than an expensive one needing its own validation — and an
/// embedding-based version would inherit the very retrieval machinery whose failures it exists to
/// detect, which is how a metric ends up unable to fail.
/// </para>
/// </remarks>
internal static class LongMemEvalAnswerPresence
{
    /// <summary>
    /// Tokens too common to carry evidence. An answer made only of these cannot be checked this way,
    /// which is reported as <see cref="LongMemEvalAnswerPresenceResult.Checkable"/> = false rather
    /// than silently passing.
    /// </summary>
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "the", "and", "of", "a", "an", "to", "in", "is", "it", "for", "on", "at", "by", "with",
        "was", "were", "be", "been", "as", "that", "this", "from", "or", "yes", "no", "not",
        "he", "she", "they", "you", "i", "we", "his", "her", "their", "your", "my", "our",
    };

    /// <summary>Minimum distinctive tokens an answer needs before absence means anything.</summary>
    private const int MinimumCheckableTokens = 1;

    /// <summary>Fraction of distinctive answer tokens that must appear for the answer to count present.</summary>
    private const double PresenceThreshold = 0.5;

    internal static LongMemEvalAnswerPresenceResult Evaluate(
        string? goldAnswer,
        IReadOnlyCollection<string> storedMemoryText)
    {
        ArgumentNullException.ThrowIfNull(storedMemoryText);

        var answerTokens = Tokenize(goldAnswer)
            .Where(token => !Stopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // An answer with no distinctive tokens ("yes", "the and of a") cannot be located by overlap.
        // Reported as not checkable AND not present: an unmeasurable question must never count as
        // passing, which is precisely how a metric stops being able to fail.
        if (answerTokens.Length < MinimumCheckableTokens)
            return new LongMemEvalAnswerPresenceResult(false, false, [], 0);

        var memoryTokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in storedMemoryText)
            foreach (var token in Tokenize(text))
                memoryTokens.Add(token);

        var matched = answerTokens.Where(memoryTokens.Contains).ToArray();
        var coverage = (double)matched.Length / answerTokens.Length;

        return new LongMemEvalAnswerPresenceResult(
            Checkable: true,
            Present: coverage >= PresenceThreshold,
            MatchedTokens: matched,
            Coverage: coverage);
    }

    /// <summary>Lowercase alphanumeric runs. Punctuation and case carry no evidence here.</summary>
    private static IEnumerable<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var isWord = i < text.Length && char.IsLetterOrDigit(text[i]);
            if (isWord && start < 0)
            {
                start = i;
            }
            else if (!isWord && start >= 0)
            {
                yield return text[start..i].ToLowerInvariant();
                start = -1;
            }
        }
    }
}

/// <summary>The gate's verdict for one question.</summary>
/// <param name="Checkable">
/// False when the gold answer has no distinctive tokens. Kept separate from <paramref name="Present"/>
/// so "we cannot tell" is never reported as "the answer is missing", and never as "it is there".
/// </param>
/// <param name="Present">Whether enough of the answer's distinctive tokens appear in stored memory.</param>
/// <param name="MatchedTokens">Which tokens matched — so a partial result can be inspected, not guessed at.</param>
/// <param name="Coverage">Matched fraction of distinctive answer tokens.</param>
public sealed record LongMemEvalAnswerPresenceResult(
    bool Checkable,
    bool Present,
    IReadOnlyList<string> MatchedTokens,
    double Coverage);
