using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// Deciding whether a capture-side change is worth measuring, before paying for the run (8.3c).
/// </summary>
/// <remarks>
/// <para>
/// The question 8.3b asks costs ~96M input tokens. This asks the cheap version first: a setting that
/// stores <i>more</i> can only convert a failure where something needed was <b>never stored</b>. A
/// failure whose gold answer was already in the assembled context is a retrieval or answering failure,
/// and capturing more makes the context bigger without making it right.
/// </para>
/// <para>
/// Deterministic and provider-free — it runs over the presence signal already recorded on every
/// question, which is what makes it the cheapest rung of an expensive decision.
/// </para>
/// </remarks>
public sealed class LongMemEvalCaptureHeadroomTests
{
    private static CaptureHeadroomQuestion Q(
        string id, bool correct, bool checkable = true, bool present = false, string type = "episodic") =>
        new(id, type, correct, checkable, present);

    [Fact]
    public void AFailureWhoseAnswerWasAlreadyInContextIsNotCaptureReachable()
    {
        // THE property. Nothing was missing, so storing more cannot convert it -- it only enlarges the
        // context, which the measured cost says is not free.
        var summary = LongMemEvalCaptureHeadroom.Summarise([
            Q("a", correct: false, present: true),
            Q("b", correct: false, present: true),
        ]).Single();

        summary.Failures.Should().Be(2);
        summary.FailuresAnswerPresent.Should().Be(2);
        summary.CaptureReachableFailures.Should().Be(0);
        summary.CaptureCeiling.Should().Be(0);
        LongMemEvalCaptureHeadroom.WorthMeasuring(summary).Should().BeFalse();
    }

    [Fact]
    public void AFailureWhoseAnswerWasAbsentIsTheOnlyKindCaptureCouldConvert()
    {
        var summary = LongMemEvalCaptureHeadroom.Summarise([
            Q("a", correct: false, present: false),
            Q("b", correct: true),
            Q("c", correct: true),
            Q("d", correct: true),
        ]).Single();

        summary.CaptureReachableFailures.Should().Be(1);
        summary.CaptureCeiling.Should().Be(0.25);
        summary.Accuracy.Should().Be(0.75);
        LongMemEvalCaptureHeadroom.WorthMeasuring(summary).Should().BeTrue();
    }

    [Fact]
    public void AnUncheckableFailureIsNotCountedAsHeadroom()
    {
        // It would inflate exactly the number used to justify spending. A gold answer with no
        // distinctive tokens tells us nothing about whether anything was missing.
        var summary = LongMemEvalCaptureHeadroom.Summarise([
            Q("a", correct: false, checkable: false),
            Q("b", correct: false, checkable: false),
        ]).Single();

        summary.Failures.Should().Be(2);
        summary.FailuresCheckable.Should().Be(0);
        summary.CaptureReachableFailures.Should().Be(0);
        LongMemEvalCaptureHeadroom.WorthMeasuring(summary).Should().BeFalse();
    }

    [Fact]
    public void CorrectAnswersAreNeverCountedAsFailuresHoweverTheirPresenceReads()
    {
        // A correct answer whose presence gate says "absent" is a gate limitation, not headroom.
        var summary = LongMemEvalCaptureHeadroom.Summarise([
            Q("a", correct: true, present: false),
            Q("b", correct: true, present: false),
        ]).Single();

        summary.Failures.Should().Be(0);
        summary.CaptureReachableFailures.Should().Be(0);
        summary.Accuracy.Should().Be(1);
    }

    [Fact]
    public void TypesAreReportedSeparatelyAndNeverPooled()
    {
        // Pooling is what diluted the episodic signal ~8x in the decision that set the current default:
        // episodic was 6 of 50 questions, so an episodic gain was averaged away before it reached the
        // number anyone looked at.
        var summary = LongMemEvalCaptureHeadroom.Summarise([
            Q("a", correct: false, present: false, type: "episodic"),
            Q("b", correct: true, type: "semantic"),
            Q("c", correct: true, type: "semantic"),
        ]);

        summary.Should().HaveCount(2);
        summary.Single(t => t.MemoryType == "episodic").CaptureCeiling.Should().Be(1);
        summary.Single(t => t.MemoryType == "semantic").CaptureCeiling.Should().Be(0);
    }

    [Fact]
    public void TheThresholdIsOneWholeQuestionNotAPercentage()
    {
        // Accuracy on an n-question sample moves in steps of 1/n, so a ceiling below one whole question
        // cannot clear any noise band whatever the run reports. A percentage threshold would look
        // scale-free and hide the quantisation that actually decides the outcome.
        var thirty = Enumerable.Range(0, 30)
            .Select(index => Q($"q{index}", correct: index > 0, present: true))
            .ToList();

        var summary = LongMemEvalCaptureHeadroom.Summarise(thirty).Single();

        summary.Failures.Should().Be(1);
        summary.CaptureReachableFailures.Should().Be(0, "the single failure had its answer present");
        LongMemEvalCaptureHeadroom.WorthMeasuring(summary).Should().BeFalse();
    }

    [Fact]
    public void AnEmptyTypeReportsZeroRatherThanDividingByZero()
    {
        LongMemEvalCaptureHeadroom.Summarise([]).Should().BeEmpty();
        new CaptureHeadroomType("episodic", 0, 0, 0, 0).CaptureCeiling.Should().Be(0);
        new CaptureHeadroomType("episodic", 0, 0, 0, 0).Accuracy.Should().Be(0);
    }
}
