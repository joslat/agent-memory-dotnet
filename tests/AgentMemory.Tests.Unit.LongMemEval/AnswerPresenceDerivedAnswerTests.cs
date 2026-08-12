using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// A gold answer that is purely a number is <b>uncheckable</b> by token overlap, not absent.
/// </summary>
/// <remarks>
/// Found by reconciling two questions where this gate and the judge disagreed: <c>eeda8a6d</c>
/// ("17 fish total") and <c>1f2b8d4f</c> ("$750"). Both were judged CORRECT while the gate reported
/// the answer absent from memory. Neither was wrong — the number is <i>derived</i> from stored
/// evidence and was never itself written down, so overlap cannot find it. Reporting that as absent
/// blames extraction for an answer the model computed correctly.
/// </remarks>
public sealed class AnswerPresenceDerivedAnswerTests
{
    private static readonly string[] Memory =
    [
        "user owns 5 golden honey gouramis",
        "user owns 1 small pleco catfish",
        "user owns 10 neon tetras",
    ];

    [Theory]
    [InlineData("17")]
    [InlineData("750")]
    [InlineData("$750")]
    [InlineData("17.")]
    public void APurelyNumericGoldAnswerIsUncheckableRatherThanAbsent(string goldAnswer)
    {
        var result = LongMemEvalAnswerPresence.Evaluate(goldAnswer, Memory);

        result.Checkable.Should().BeFalse(
            "the number is derived from stored evidence and was never itself stored");
        result.Present.Should().BeFalse(
            "an unmeasurable question must never count as passing either");
    }

    [Fact]
    public void AnAnswerWithLexicalContentIsStillChecked()
    {
        // The guard must stay narrow: a number ALONGSIDE distinctive words is still checkable, because
        // the words carry evidence even when the numeral does not.
        var result = LongMemEvalAnswerPresence.Evaluate("10 neon tetras", Memory);

        result.Checkable.Should().BeTrue();
        result.Present.Should().BeTrue();
    }

    [Fact]
    public void AGenuinelyAbsentTextAnswerIsStillReportedAbsent()
    {
        // The floor still has to fire. Widening "uncheckable" until nothing is ever absent would make
        // this gate unable to fail, which is the exact defect shape it exists to prevent.
        var result = LongMemEvalAnswerPresence.Evaluate("a saltwater reef tank", Memory);

        result.Checkable.Should().BeTrue();
        result.Present.Should().BeFalse();
    }
}
