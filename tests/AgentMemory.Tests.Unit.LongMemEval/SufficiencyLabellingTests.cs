using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// Where "was this question answerable?" comes from — and why it is not the presence gate.
/// </summary>
/// <remarks>
/// <para>
/// The sufficiency AUC asks whether retrieval confidence ranks answerable questions above
/// unanswerable ones, so it needs a <b>ground truth</b> for answerability. The first version used the
/// answer-presence gate, which asks whether the gold answer's distinctive tokens appear anywhere in
/// memory — a deliberately cheap floor for spotting extraction failure.
/// </para>
/// <para>
/// Measured on an abstention-enriched sample, that gate reported <b>3 of 4</b> abstention questions as
/// "present". Their topic <i>is</i> discussed in the conversation even though the specific fact is
/// not, so the tokens are there. A heuristic floor for extraction failure is not a ground truth for
/// answerability, and using it as one put most of the unanswerable class on the wrong side of every
/// comparison.
/// </para>
/// <para>
/// The dataset already knows: an <c>_abs</c> question is unanswerable by definition. That label is
/// free, exact, and needs no gate.
/// </para>
/// </remarks>
public sealed class SufficiencyLabellingTests
{
    private static LongMemEvalQuestionTelemetry Question(
        int number,
        double? signal,
        bool isAbstention = false,
        bool checkable = true,
        bool present = true) =>
        new(number, MessagesStored: 10, ItemsRetrieved: 5, RecallTruncated: false, Status: "ok")
        {
            SufficiencySignal = signal,
            IsAbstention = isAbstention,
            // (Checkable, Present) -- in that order. Getting it backwards here made a checkable
            // missing answer read as uncheckable, and the test caught it.
            AnswerPresence = new LongMemEvalAnswerPresenceResult(checkable, present, [], 0),
        };

    private static dynamic Report(params LongMemEvalQuestionTelemetry[] questions) =>
        LongMemEvalSufficiencyReport.From(questions);

    [Fact]
    public void AnAbstentionQuestionCountsAsUnanswerableEvenWhenTheGateSaysPresent()
    {
        // THE case, and it is the common one: 3 of 4 abstention questions in a real run had their
        // gold answer's tokens present in memory. Deferring to the gate put them on the answerable
        // side and destroyed the class the AUC exists to order against.
        var report = Report(
            Question(1, 0.5, isAbstention: true, present: true),
            Question(2, 0.9, present: true));

        ((int)report.presentCount).Should().Be(1);
        ((int)report.absentCount).Should().Be(1);
        ((double?)report.auc).Should().Be(1.0, "the answerable question scored higher");
    }

    [Fact]
    public void AnAbstentionQuestionNeedsNoCheckableGoldAnswer()
    {
        // Its unanswerability is a dataset fact, not a measurement. Requiring the gate to confirm it
        // would drop exactly the questions that make the metric computable -- an abstention answer is
        // often phrased in common words with no distinctive tokens to find.
        var report = Report(
            Question(1, 0.5, isAbstention: true, checkable: false, present: false),
            Question(2, 0.9, present: true));

        ((int)report.absentCount).Should().Be(1);
        ((int)report.abstentionQuestions).Should().Be(1);
    }

    [Fact]
    public void AnOrdinaryQuestionStillNeedsTheGate()
    {
        // The other half. For a normal question the dataset asserts nothing about whether OUR
        // extraction stored the answer, so the gate remains the only evidence -- and an unmeasurable
        // one is excluded rather than guessed.
        var report = Report(
            Question(1, 0.9, present: true),
            Question(2, 0.5, checkable: false, present: false),
            Question(3, 0.4, present: false));

        ((int)report.presentCount).Should().Be(1);
        ((int)report.absentCount).Should().Be(1);
        ((int)report.excludedNotCheckable).Should().Be(1);
    }

    [Fact]
    public void AnAbstentionQuestionIsNeverCountedAsUncheckable()
    {
        // It was not excluded, so reporting it under an exclusion count would make the denominators
        // stop adding up -- and the denominators are the only way to judge whether the AUC means
        // anything.
        var report = Report(Question(1, 0.5, isAbstention: true, checkable: false));

        ((int)report.excludedNotCheckable).Should().Be(0);
    }

    [Fact]
    public void TheAbstentionCountIsReportedSoAZeroIsVisible()
    {
        // When it is 0 the unanswerable class came entirely from accidental extraction misses, which
        // is how a 50-question run ended up with ONE absent observation and two arms reporting 0.969
        // and 0.094 from the same corpus. A reader must be able to see that without recounting.
        ((int)Report(Question(1, 0.9), Question(2, 0.4, present: false)).abstentionQuestions)
            .Should().Be(0);
    }

    [Fact]
    public void AQuestionWithNoSignalIsExcludedWhateverItsLabel()
    {
        // Diagnostics off means the signal was never collected. An abstention label cannot substitute
        // for the measurement the comparison is actually about.
        var report = Report(
            Question(1, null, isAbstention: true),
            Question(2, 0.9, present: true));

        ((int)report.absentCount).Should().Be(0);
        ((int)report.excludedNoSignal).Should().Be(1);
    }
}
