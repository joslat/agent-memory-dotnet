using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// The cost guard after it was refined from "nothing went wrong" to "nothing is unaccounted for".
/// </summary>
/// <remarks>
/// The original guard required exactly the planned number of provider calls and zero failures. That
/// is not a property a 614-call run over a network can hold, and it is incompatible with the
/// extractor's own recovery paths — a parse retry re-prompts, a batch split re-sends the halves, and
/// both legitimately add calls. Three consecutive 15–40 minute preparations died on it.
/// <para>
/// Refining a guard is only defensible if it still catches what it was for. These tests exist to
/// show that it does: <see cref="AnExcessWithNoRecordedRecoveryIsStillRejected"/> is the case the
/// guard really protects, and it must fail. Correctness is not this guard's job — the session-set
/// comparison beside it proves every planned session persisted, in order, and is unchanged.
/// </para>
/// </remarks>
public sealed class BatchAccountingGuardTests
{
    private const int Planned = 12;

    [Fact]
    public void ExactlyThePlannedCallsIsAccepted()
    {
        Accept(successful: 12, unified: 12).Should().BeTrue();
    }

    [Fact]
    public void AnExcessWithNoRecordedRecoveryIsStillRejected()
    {
        // The load-bearing case. Four calls appeared that no split and no retry accounts for: that
        // is unexplained provider work against a sealed manifest, and it is exactly what this guard
        // exists to catch. If refining it had removed this, the guard would be decoration.
        Accept(successful: 16, unified: 16).Should().BeFalse();
    }

    [Fact]
    public void AnExcessExplainedByARecordedSplitIsAccepted()
    {
        // The failure that motivated the refinement: a genuine parse-or-format split, doing what it
        // is designed to do, produced 16 successful calls against 12 planned.
        Accept(successful: 16, unified: 16, splits: 1).Should().BeTrue();
    }

    [Fact]
    public void AnExcessExplainedByARecordedRetryIsAccepted()
    {
        Accept(successful: 14, unified: 14, retries: 2).Should().BeTrue();
    }

    [Fact]
    public void FewerCallsThanPlannedIsRejected()
    {
        // Under-running is never explainable: a batch that never ran cannot have been recovered.
        Accept(successful: 11, unified: 11, splits: 1, retries: 5).Should().BeFalse();
    }

    [Fact]
    public void ACallOfAnUnexpectedPurposeIsRejectedEvenWhenRecoveryIsRecorded()
    {
        // Purpose is not something recovery explains. A split re-sends unified batches; it never
        // produces a call of some other kind, so this stays a hard failure.
        Accept(successful: 12, unified: 12, other: 1, splits: 1).Should().BeFalse();
    }

    [Fact]
    public void MissingSplitDiagnosticsFailClosed()
    {
        // BatchSplitCount is optional on the adapter options, so a harness that never wired it
        // reports zero splits. An excess must then read as unexplained rather than as innocent.
        Accept(successful: 16, unified: 16, splits: 0, retries: 0).Should().BeFalse();
    }

    private static bool Accept(
        long successful, long unified, long other = 0, long splits = 0, long retries = 0) =>
        AgentMemoryLongMemEvalAdapter.IsBatchAccountingAcceptable(
            successful, unified, other, splits, retries, Planned);
}
