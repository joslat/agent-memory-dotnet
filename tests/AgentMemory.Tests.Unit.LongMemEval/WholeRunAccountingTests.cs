using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// The whole-run accounting guard must agree with its per-question sibling.
/// </summary>
/// <remarks>
/// The per-question guard was refined to accept excess provider calls that a recorded split or retry
/// explains, because demanding exactly N calls is demanding that nothing ever went wrong — which the
/// extractor's own recovery paths violate. The whole-run guard was left demanding exact equality.
/// <para>
/// So an n=50 cold rebuild ran 682 calls against 680 planned, with <b>failures 0 and retries 0</b>,
/// and died after 75 minutes. Split sub-calls execute under their own Activity, so they are invisible
/// to the activity-based retry counter: a split shows up as bare extra calls that neither counter can
/// account for. One guard accepted them and the other rejected them.
/// </para>
/// <para>
/// Both now route through the same decision, so they cannot drift apart again — which is the actual
/// defect here, not the arithmetic.
/// </para>
/// </remarks>
public sealed class WholeRunAccountingTests
{
    private const int Planned = 680;

    [Fact]
    public void ExactlyThePlannedCallsIsAccepted()
    {
        Accept(calls: 680, splits: 0).Should().BeTrue();
    }

    [Fact]
    public void ExcessExplainedByARecordedSplitIsAccepted()
    {
        // The failure that cost a 75-minute rebuild: one split turns a planned call into a failed
        // whole-batch attempt plus two halves, so the run lands two calls over with nothing in the
        // retry or failure counters to show for it.
        Accept(calls: 682, splits: 1).Should().BeTrue();
    }

    [Fact]
    public void ExcessWithNoRecordedRecoveryIsStillRejected()
    {
        // The property the guard exists for. Unexplained provider work against a sealed manifest
        // must still fail, or relaxing the guard would have removed it rather than corrected it.
        Accept(calls: 682, splits: 0).Should().BeFalse();
    }

    [Fact]
    public void FewerCallsThanPlannedIsAlwaysRejected()
    {
        // Under-running is never explainable: a batch that never ran cannot have been recovered.
        Accept(calls: 679, splits: 5).Should().BeFalse();
    }

    private static bool Accept(int calls, long splits) =>
        AgentMemoryLongMemEvalAdapter.IsBatchAccountingAcceptable(
            successfulCalls: calls,
            successfulUnifiedBatchCalls: calls,
            otherCalls: 0,
            recordedSplits: splits,
            recordedRetries: 0,
            plannedBatchCount: Planned);
}
