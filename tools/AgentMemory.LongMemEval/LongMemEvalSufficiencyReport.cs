namespace AgentMemory.LongMemEval;

/// <summary>
/// Pairs each question's sufficiency signal with whether its answer was stored, and reports the AUC.
/// </summary>
/// <remarks>
/// <para>
/// The pairing is where this can quietly go wrong, so the exclusions are explicit rather than
/// incidental. A question contributes only when <b>both</b> halves are real: a signal that was
/// actually collected, and a presence verdict that was actually checkable. Substituting a default for
/// either — 0 for an uncollected signal, "absent" for an uncheckable answer — would populate the AUC
/// with observations that carry no information and drag it towards 0.5, which is the kill line. A
/// metric computed over absent data is a metric that can lie.
/// </para>
/// <para>
/// The excluded counts travel with the result for the same reason: an AUC over 6 of 50 questions is
/// not the run's AUC, and the bare number cannot say so.
/// </para>
/// </remarks>
internal static class LongMemEvalSufficiencyReport
{
    internal static object From(IReadOnlyList<LongMemEvalQuestionTelemetry> telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);

        // An abstention question is unanswerable BY DEFINITION -- the dataset says so -- and needs no
        // gate to establish it. Everything else is labelled by the presence gate, which requires a
        // checkable gold answer.
        //
        // The order matters and was wrong at first. Deferring to the gate for abstention questions
        // labelled 3 of 4 of them "answerable", because their topic IS discussed in the conversation
        // even though the specific fact is not, so the gold answer's tokens are present. A heuristic
        // floor for extraction failure is not a ground truth for answerability, and using it as one
        // put most of the unanswerable class on the wrong side of the comparison.
        var usable = telemetry
            .Where(item => item.SufficiencySignal is not null
                && (item.IsAbstention || item.AnswerPresence is { Checkable: true }))
            .ToArray();

        var result = LongMemEvalSufficiencyAuc.Compute(
            [.. usable.Select(item => new LongMemEvalSufficiencyAuc.Observation(
                item.SufficiencySignal!.Value,
                AnswerPresent: !item.IsAbstention && item.AnswerPresence!.Present))]);

        return new
        {
            auc = result.Auc,
            presentCount = result.PresentCount,
            absentCount = result.AbsentCount,
            tiedObservations = result.TiedObservations,
            justifiesAbstentionWork = result.JustifiesAbstentionWork,
            summary = result.Describe(),
            // Every question that could not contribute, and why. Without these the AUC's denominator
            // is unauditable -- and it is the denominator, not the number, that decides whether the
            // result means anything.
            // How much of the unanswerable class came from the dataset's own label rather than from
            // an extraction failure. When this is 0 the AUC is resting on accidental misses, which is
            // how a 50-question run ended up with a single absent observation.
            abstentionQuestions = usable.Count(item => item.IsAbstention),
            excludedNoSignal = telemetry.Count(item => item.SufficiencySignal is null),
            excludedNotCheckable = telemetry.Count(
                item => item.SufficiencySignal is not null
                     && !item.IsAbstention
                     && item.AnswerPresence is null or { Checkable: false }),
            questionsConsidered = telemetry.Count,
        };
    }
}
