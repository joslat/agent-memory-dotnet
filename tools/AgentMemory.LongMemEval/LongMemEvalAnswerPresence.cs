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

    /// <summary>
    /// Groups gate verdicts by question type, because <b>"absent" means two different things</b>.
    /// </summary>
    /// <remarks>
    /// Adjudicated 2026-08-10: of two questions reported absent, one was a real extraction failure
    /// (<c>eeda8a6d</c> — the agent itself said the data was missing, and the judge agreed) and one
    /// was not (<c>1f2b8d4f</c> — answered correctly from memory alone, but its gold answer is a
    /// <b>computed</b> value: 750 = 800 − 50, and memory stores the two prices, never the difference).
    /// <para>
    /// The gate is therefore a floor for <b>extractive</b> answers. Grouping by type separates
    /// "absent because not stored" from "absent because derived" <b>without changing the metric</b> —
    /// each verdict is reported exactly as measured, and only the axis needed to read it is added.
    /// </para>
    /// <para>
    /// Uncheckable questions are counted apart from absent ones on purpose. Folding "we cannot tell"
    /// into "it is missing" is what turns a floor into a false alarm.
    /// </para>
    /// </remarks>
    internal static IReadOnlyDictionary<string, LongMemEvalAnswerPresenceGroup> SummariseByType(
        IReadOnlyList<LongMemEvalQuestionTelemetry> telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);

        var groups = new Dictionary<string, LongMemEvalAnswerPresenceGroup>(StringComparer.Ordinal);
        foreach (var item in telemetry)
        {
            // No gate result means the probe never ran on this question. That is not evidence of
            // presence OR absence, so it is excluded rather than counted as either.
            if (item.AnswerPresence is not { } presence)
                continue;

            // Never dropped for lacking a type: a shrinking denominator is how a metric stops adding up.
            var key = string.IsNullOrWhiteSpace(item.QuestionType) ? "unknown" : item.QuestionType!;
            var current = groups.TryGetValue(key, out var existing)
                ? existing
                : new LongMemEvalAnswerPresenceGroup(0, 0, 0, 0);

            groups[key] = new LongMemEvalAnswerPresenceGroup(
                Total: current.Total + 1,
                Checkable: current.Checkable + (presence.Checkable ? 1 : 0),
                Present: current.Present + (presence.Present ? 1 : 0),
                Absent: current.Absent + (presence.Checkable && !presence.Present ? 1 : 0));
        }

        return groups;
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
/// <summary>Gate verdicts for one question type.</summary>
/// <param name="Total">Questions of this type that the probe ran on.</param>
/// <param name="Checkable">Those whose gold answer had distinctive tokens to look for.</param>
/// <param name="Present">Those whose answer material was found in memory.</param>
/// <param name="Absent">
/// Checkable and not present. For derived-answer types this is EXPECTED, not a failure — see the
/// remarks on <see cref="LongMemEvalAnswerPresence.SummariseByType"/>.
/// </param>
internal sealed record LongMemEvalAnswerPresenceGroup(
    int Total,
    int Checkable,
    int Present,
    int Absent);