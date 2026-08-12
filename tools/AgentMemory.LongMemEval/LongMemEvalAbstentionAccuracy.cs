namespace AgentMemory.LongMemEval;

/// <summary>
/// How well the agent says <b>"I don't know"</b> — the only meta-memory score this dataset offers.
/// </summary>
/// <remarks>
/// <para>
/// An abstention question is one the dataset declares unanswerable. Getting it right means declining
/// to answer; getting it wrong means producing an answer anyway, which is hallucination under absence
/// — the failure mode a memory system is least able to detect in itself.
/// </para>
/// <para>
/// <b>Not the same thing as the AUC's "absent" count</b>, though both concern unanswerable questions,
/// and conflating them is easy. The AUC's absent count is an <i>input</i>: the ground-truth label
/// against which retrieval confidence is ordered, and it is identical across arms by construction.
/// This is an <i>outcome</i>: whether the agent behaved correctly on those questions. Reporting only
/// the first invites reading a class balance as a result.
/// </para>
/// <para>
/// Worth reporting on its own line because the project's taxonomy names abstention as the only place
/// LongMemEval scores meta-memory at all — and across 52 recorded runs before typed sampling shipped,
/// not one abstention question had ever been drawn, so this had never been measured.
/// </para>
/// </remarks>
internal static class LongMemEvalAbstentionAccuracy
{
    /// <summary>Correct-answer rates for the abstention and ordinary halves of a run.</summary>
    /// <param name="AbstentionCorrect">Abstention questions the judge scored correct.</param>
    /// <param name="AbstentionTotal">Abstention questions judged at all.</param>
    /// <param name="OrdinaryCorrect">Ordinary questions the judge scored correct.</param>
    /// <param name="OrdinaryTotal">Ordinary questions judged at all.</param>
    internal readonly record struct Result(
        int AbstentionCorrect, int AbstentionTotal, int OrdinaryCorrect, int OrdinaryTotal)
    {
        /// <summary>Abstention accuracy, or null when no abstention question was drawn.</summary>
        /// <remarks>
        /// Null rather than 0 or 1: a sample containing no abstention question has not measured
        /// meta-memory badly, it has not measured it. Before typed sampling shipped that was every run.
        /// </remarks>
        internal double? AbstentionAccuracy =>
            AbstentionTotal == 0 ? null : (double)AbstentionCorrect / AbstentionTotal;

        internal double? OrdinaryAccuracy =>
            OrdinaryTotal == 0 ? null : (double)OrdinaryCorrect / OrdinaryTotal;

        /// <summary>
        /// Abstention questions answered when they should have been declined.
        /// </summary>
        /// <remarks>
        /// Named for the behaviour rather than as "incorrect", because this specific wrong answer is
        /// the interesting one: the agent asserted something memory did not hold.
        /// </remarks>
        internal int AnsweredWhenItShouldHaveAbstained => AbstentionTotal - AbstentionCorrect;
    }

    /// <summary>
    /// Scores the two halves from per-question telemetry and the judge's own verdicts.
    /// </summary>
    /// <remarks>
    /// A question with no judge verdict is excluded from both denominators rather than counted wrong:
    /// an unjudged question measured nothing, and folding it in as a failure would understate every
    /// rate by however many the judge could not parse.
    /// </remarks>
    internal static Result Score(
        IReadOnlyList<LongMemEvalQuestionTelemetry> telemetry,
        Func<string?, bool?> verdictFor)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(verdictFor);

        int abstentionCorrect = 0, abstentionTotal = 0, ordinaryCorrect = 0, ordinaryTotal = 0;
        foreach (var item in telemetry)
        {
            if (verdictFor(item.QuestionId) is not { } correct) continue;
            if (item.IsAbstention)
            {
                abstentionTotal++;
                if (correct) abstentionCorrect++;
            }
            else
            {
                ordinaryTotal++;
                if (correct) ordinaryCorrect++;
            }
        }

        return new Result(abstentionCorrect, abstentionTotal, ordinaryCorrect, ordinaryTotal);
    }
}
