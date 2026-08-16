namespace AgentMemory.LongMemEval;

/// <summary>
/// 27.3. The questions that a <b>perfect-context oracle</b> never answers correctly, and the evidence
/// that put each one on the list.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is for.</b> A question the model gets wrong when handed exactly the evidence the
/// dataset says answers it cannot be fixed by any memory system. Leaving such questions in the
/// denominator caps the achievable score below 100% for reasons that have nothing to do with memory,
/// and — worse — makes a real improvement look smaller than it is.
/// </para>
/// <para>
/// <b>Excluded here means reported separately, never deleted.</b> Both denominators travel together in
/// every report: the raw score over all questions, and the improvable score over the rest. Silently
/// dropping questions from a benchmark is how numbers stop meaning anything, and a reader who
/// disagrees with an exclusion must be able to see and undo it.
/// </para>
/// <para>
/// <b>How the list was actually built, including the part that was wrong.</b> An earlier writeup named
/// four questions as "0/36 with perfect context". The archive does not support that: the oracle had
/// never been pointed at any of them, and all 36 attempts were <i>retrieval</i> runs, where a wrong
/// answer is ambiguous between "unanswerable" and "not retrieved". Two of the four named questions
/// turned out not to belong here at all — <c>031748ae_abs</c> scores 3/4 with perfect context and
/// <c>gpt4_8279ba03</c> scores 4/4, the latter being a pure retrieval miss. Two questions that were
/// never suspected, <c>bf659f65</c> and <c>7a8d0b71</c>, do belong.
/// </para>
/// <para>
/// <b>The pattern worth noticing.</b> Three of the four are <c>single-session-assistant</c> — questions
/// whose answer was stated by the assistant rather than the user. That is the smallest question type in
/// the set, and it holds three quarters of the oracle-impossible questions. It is a property of the
/// benchmark, not of any system measured against it.
/// </para>
/// </remarks>
internal static class LongMemEvalOracleImpossible
{
    /// <summary>
    /// Question ids never answered correctly by the perfect-context oracle, with the run that proved it.
    /// </summary>
    /// <remarks>
    /// Every entry is 0 correct in 8 independent attempts against gold-only context, zero distractors,
    /// no retrieval involved — <c>--oracle-precision --distractor-sessions 0 --gold-fraction 1.0</c>,
    /// artifacts <c>oracle-impossible-probe-r1..r8.json</c>. Under a coin-flip null, 0-of-8 is p≈0.004
    /// per question; the four together are not a sampling accident.
    /// </remarks>
    internal static readonly IReadOnlyDictionary<string, string> Questions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["352ab8bd"] =
                "single-session-assistant. 0/8 with perfect context. The gold answer is a number stated "
                + "by the assistant; the oracle sees the turn and still does not produce it.",
            ["58470ed2"] =
                "single-session-assistant. 0/8 with perfect context.",
            ["7a8d0b71"] =
                "single-session-assistant. 0/8 with perfect context. Not previously suspected — it "
                + "surfaced only once the oracle was made targetable by question id.",
            ["bf659f65"] =
                "multi-session. 0/8 with perfect context, over the largest gold context in the set "
                + "(38k characters across 3 gold sessions). Not previously suspected.",
        };

    internal static bool IsImpossible(string questionId) => Questions.ContainsKey(questionId);

    /// <summary>
    /// Both denominators for a set of judged results: the raw score, and the score over questions a
    /// memory system could in principle get right.
    /// </summary>
    internal static LongMemEvalImprovableScore Score(IReadOnlyDictionary<string, bool> correctByQuestionId)
    {
        ArgumentNullException.ThrowIfNull(correctByQuestionId);

        var excluded = correctByQuestionId.Keys.Where(IsImpossible).OrderBy(id => id, StringComparer.Ordinal).ToList();

        // Counted, not assumed. If an excluded question is ever answered correctly, the exclusion is
        // wrong and the report must say so rather than quietly discard the evidence against itself.
        var excludedCorrect = excluded.Count(id => correctByQuestionId[id]);

        return new LongMemEvalImprovableScore(
            TotalQuestions: correctByQuestionId.Count,
            TotalCorrect: correctByQuestionId.Values.Count(correct => correct),
            ExcludedQuestionIds: excluded,
            ExcludedAnsweredCorrectly: excludedCorrect);
    }
}

/// <summary>Raw and improvable accuracy, reported side by side and never one without the other.</summary>
internal sealed record LongMemEvalImprovableScore(
    int TotalQuestions,
    int TotalCorrect,
    IReadOnlyList<string> ExcludedQuestionIds,
    int ExcludedAnsweredCorrectly)
{
    public int ImprovableQuestions => TotalQuestions - ExcludedQuestionIds.Count;

    public int ImprovableCorrect => TotalCorrect - ExcludedAnsweredCorrectly;

    public double? RawAccuracy =>
        TotalQuestions == 0 ? null : (double)TotalCorrect / TotalQuestions;

    public double? ImprovableAccuracy =>
        ImprovableQuestions <= 0 ? null : (double)ImprovableCorrect / ImprovableQuestions;

    /// <summary>
    /// Set when an oracle-impossible question was answered correctly anyway, which falsifies its
    /// exclusion.
    /// </summary>
    /// <remarks>
    /// The list is a claim about the world and can be wrong. A run that contradicts it must surface
    /// that contradiction loudly, because the failure mode of a curated exclusion list is that it
    /// quietly becomes a way of not counting inconvenient questions.
    /// </remarks>
    public string? ExclusionContradicted => ExcludedAnsweredCorrectly == 0
        ? null
        : $"{ExcludedAnsweredCorrectly} question(s) on the oracle-impossible list were answered "
          + "correctly in this run. Re-run the oracle probe for them and remove any that are solvable.";
}
