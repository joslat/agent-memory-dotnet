using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// The checkpoint and the full preparation judge extraction accounting with one decision.
/// </summary>
/// <remarks>
/// <para>
/// The first Bitdeer checkpoint (2026-09-29) made 15 calls against 13 planned, and all 15 replies were
/// valid strict JSON. One reply acknowledged only 1 of its batch's 4 source sessions; the library
/// rejected it and split the batch 4 → 2 + 2, which is the two extra calls. The full preparation
/// accepts exactly that, because a recorded split explains the excess. The checkpoint demanded an exact
/// count, so it rejected a clean run and printed no projection.
/// </para>
/// <para>
/// Excess nobody can account for must still fail: it means the frozen plan did not describe the run.
/// </para>
/// </remarks>
public sealed class CheckpointAccountingTests
{
    [Fact]
    public void ExcessCallsExplainedByARecordedSplitAreAccepted() =>
        LongMemEvalPreparedPairProgram.IsExtractionAccountingAcceptable(
                Snapshot(calls: 15, completed: 15), recordedSplits: 1, plannedCalls: 13, maxConcurrency: 12)
            .Should().BeTrue("the 2026-09-29 checkpoint was exactly this: 13 planned, one split, 15 made");

    [Fact]
    public void UnexplainedExcessStillFails() =>
        LongMemEvalPreparedPairProgram.IsExtractionAccountingAcceptable(
                Snapshot(calls: 15, completed: 15), recordedSplits: 0, plannedCalls: 13, maxConcurrency: 12)
            .Should().BeFalse();

    [Fact]
    public void AnExactRunIsAccepted() =>
        LongMemEvalPreparedPairProgram.IsExtractionAccountingAcceptable(
                Snapshot(calls: 13, completed: 13), recordedSplits: 0, plannedCalls: 13, maxConcurrency: 12)
            .Should().BeTrue();

    [Theory]
    [InlineData(12, 12, 4, "fewer calls succeeded than were planned")]
    [InlineData(15, 14, 4, "a started call never completed")]
    [InlineData(15, 15, 1, "no concurrency means the batches ran serially, not as planned")]
    [InlineData(15, 15, 13, "concurrency above the configured cap")]
    public void TheRemainingGuardsStillBind(int calls, int completed, int concurrency, string because) =>
        LongMemEvalPreparedPairProgram.IsExtractionAccountingAcceptable(
                Snapshot(calls, completed, concurrency), recordedSplits: 1, plannedCalls: 13, maxConcurrency: 12)
            .Should().BeFalse(because);

    /// <summary>Both guards call the shared decision; neither keeps a private copy.</summary>
    [Fact]
    public void TheCheckpointAndTheFullPreparationShareTheDecision()
    {
        var source = ToolSource("LongMemEvalPreparedPairProgram.cs");

        source.Split("IsExtractionAccountingAcceptable(").Length.Should().Be(
            4, "one definition and two call sites: the checkpoint and the full preparation");
        source.Should().NotContain("checkpointSnapshot.Calls != checkpointCalls",
            "the exact-count comparison is the drifted twin this replaced");
    }

    private static LongMemEvalChatCallSnapshot Snapshot(int calls, int completed, int concurrency = 4) =>
        new(calls, 0, TimeSpan.Zero) { CompletedCalls = completed, MaximumConcurrency = concurrency };

    private static string ToolSource(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentMemory.slnx")))
            directory = directory.Parent;
        return File.ReadAllText(Path.Combine(
            directory!.FullName, "tools", "AgentMemory.LongMemEval", fileName));
    }
}
