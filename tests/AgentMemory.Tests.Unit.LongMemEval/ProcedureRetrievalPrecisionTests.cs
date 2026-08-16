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
    public void AbstainingWhenNothingAppliesIsTheRightCall()
    {
        // THE distinction. Returning nothing when nothing applies is correct, not a failure, and
        // counting it with wrong answers would make a cautious retriever indistinguishable from a
        // reckless one.
        var scored = ProcedureRetrievalPrecision.Score([Case("t-1", [])]);

        scored.Abstained.Should().Be(1);
        scored.Missed.Should().Be(0);
        scored.WrongAtOne.Should().Be(0);
        scored.WrongProcedureRate.Should().Be(0);
    }

    [Fact]
    public void ReturningNothingWhenAProcedureAppliedIsAMissNotAnAbstention()
    {
        // This test previously asserted the opposite, under the name "AbstentionIsNotAFailure": it
        // fed a case whose correct answer was "right", retrieved nothing, and asserted that counted
        // as abstention. That encoded the defect rather than catching it.
        //
        // Returning nothing when a procedure DID apply is a miss. It is safe -- the agent
        // investigates rather than acting on the wrong plan -- but it is a failure, and scoring it in
        // the column the docs call "not a failure" meant a retriever tuned so high that it found
        // nothing would score maximally safe rather than useless.
        var scored = ProcedureRetrievalPrecision.Score([Case("t-1", [], "right")]);

        scored.Missed.Should().Be(1);
        scored.Abstained.Should().Be(0);
        scored.MissRate.Should().Be(1.0);
        scored.WrongAtOne.Should().Be(0, "a miss is still not a WRONG procedure");
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
        // t-3 applied to nothing and returned nothing (abstention); t-4 had a correct answer and
        // returned nothing (miss). One of each, not two abstentions.
        scored.AbstentionRate.Should().Be(0.25);
        scored.MissRate.Should().Be(0.25);
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
    public void TheFourOutcomesAccountForEveryTask()
    {
        // No task may fall through the classification: a total that does not reconcile would let a
        // silently dropped case look like an improved wrong-procedure rate.
        var scored = ProcedureRetrievalPrecision.Score(
        [
            Case("t-1", ["right"], "right"),
            Case("t-2", ["wrong"], "right"),
            Case("t-3", []),
        ]);

        (scored.CorrectAtOne + scored.WrongAtOne + scored.Abstained + scored.Missed)
            .Should().Be(scored.Total, "every case lands in exactly one of the four outcomes");
    }
}
