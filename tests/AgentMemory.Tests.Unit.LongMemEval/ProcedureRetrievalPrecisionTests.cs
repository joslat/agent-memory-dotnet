using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// Whether the retrieved procedure was the right one, not merely a fast one (PLAN 7.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>An agent with no procedural memory investigates; an agent with the wrong one executes.</b> The
/// second is slower to detect and more expensive to undo, and every efficiency measure a promotion
/// feature has — hit rate, steps, latency — improves when the retriever becomes more willing to
/// answer, including when it becomes more willing to answer wrongly.
/// </para>
/// </remarks>
public sealed class ProcedureRetrievalPrecisionTests
{
    private static ProcedureRetrievalCase Case(
        string taskId, string[] retrieved, params string[] correct) =>
        new(taskId, retrieved, correct);

    [Fact]
    public void TheTopResultIsWhatCounts()
    {
        // An agent acts on the first procedure it is handed. A correct one at rank 3 is a ranking
        // signal, not a success -- the agent already executed the wrong plan.
        var scored = ProcedureRetrievalPrecision.Score(
            [Case("t-1", ["wrong-a", "wrong-b", "right"], "right")]);

        scored.CorrectAtOne.Should().Be(0);
        scored.WrongAtOne.Should().Be(1);
        scored.MeanReciprocalRank.Should().BeApproximately(1d / 3, 1e-9);
    }

    [Fact]
    public void AbstentionIsNotAFailure()
    {
        // THE distinction. Returning nothing makes the agent investigate: slower, and safe. Counting
        // it with wrong answers would make a cautious retriever indistinguishable from a reckless one.
        var scored = ProcedureRetrievalPrecision.Score([Case("t-1", [], "right")]);

        scored.Abstained.Should().Be(1);
        scored.WrongAtOne.Should().Be(0);
        scored.WrongProcedureRate.Should().Be(0);
    }

    [Fact]
    public void AnsweringWhenNothingAppliesIsWrong()
    {
        // No stored procedure fits this task, so any confident retrieval is the error -- this is the
        // case where an agent executes a plan built for a different problem entirely.
        var scored = ProcedureRetrievalPrecision.Score([Case("t-1", ["some-procedure"])]);

        scored.WrongAtOne.Should().Be(1);
        scored.MeanReciprocalRank.Should().Be(0);
    }

    [Fact]
    public void PrecisionAndPrecisionWhenAnsweringMoveApart()
    {
        // Why both are reported. A retriever that answers 2 of 4 tasks and gets both right has a
        // precision@1 of 0.5 and is perfectly accurate whenever it commits. Reporting either number
        // alone hides the trade the caution is buying.
        var scored = ProcedureRetrievalPrecision.Score(
        [
            Case("t-1", ["right-1"], "right-1"),
            Case("t-2", ["right-2"], "right-2"),
            Case("t-3", []),
            Case("t-4", [], "right-4"),
        ]);

        scored.PrecisionAtOne.Should().Be(0.5);
        scored.PrecisionWhenAnswering.Should().Be(1.0);
        scored.AbstentionRate.Should().Be(0.5);
    }

    [Fact]
    public void MoreThanOneProcedureCanBeCorrect()
    {
        // Tasks legitimately have several valid approaches; scoring against a single gold id would
        // count a correct alternative as a safety failure.
        ProcedureRetrievalPrecision.Score([Case("t-1", ["b"], "a", "b")])
            .CorrectAtOne.Should().Be(1);
    }

    [Fact]
    public void AnEmptyRunScoresZeroRatherThanDividingByZero()
    {
        var scored = ProcedureRetrievalPrecision.Score([]);

        scored.Total.Should().Be(0);
        scored.PrecisionAtOne.Should().Be(0);
        scored.WrongProcedureRate.Should().Be(0);
        scored.PrecisionWhenAnswering.Should().Be(0);
    }

    [Fact]
    public void TheThreeOutcomesAccountForEveryTask()
    {
        // No task may fall through the classification: a total that does not reconcile would let a
        // silently dropped case look like an improved wrong-procedure rate.
        var scored = ProcedureRetrievalPrecision.Score(
        [
            Case("t-1", ["right"], "right"),
            Case("t-2", ["wrong"], "right"),
            Case("t-3", []),
        ]);

        (scored.CorrectAtOne + scored.WrongAtOne + scored.Abstained).Should().Be(scored.Total);
    }
}
