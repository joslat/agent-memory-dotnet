using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

public sealed class LongMemEvalPreparedVolumeLifecycleTests
{
    [Fact]
    public void CloneCannotBeginBeforeBaseContainerStops()
    {
        var lifecycle = new LongMemEvalPreparedVolumeLifecycle();
        lifecycle.BeginBasePreparation();

        var act = lifecycle.BeginClone;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*BaseMounted*Frozen*");
    }

    [Fact]
    public void FrozenBaseCanBeClonedExactlyOnce()
    {
        var lifecycle = new LongMemEvalPreparedVolumeLifecycle();
        lifecycle.BeginBasePreparation();
        lifecycle.MarkBaseContainerStopped();
        lifecycle.BeginClone();
        lifecycle.CompleteClone();

        var secondClone = lifecycle.BeginClone;

        lifecycle.State.Should().Be(LongMemEvalPreparedVolumeState.Ready);
        secondClone.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void FailedCloneReturnsToFrozenStateForSafeCleanupOrRetry()
    {
        var lifecycle = new LongMemEvalPreparedVolumeLifecycle();
        lifecycle.BeginBasePreparation();
        lifecycle.MarkBaseContainerStopped();
        lifecycle.BeginClone();

        lifecycle.FailClone();

        lifecycle.State.Should().Be(LongMemEvalPreparedVolumeState.Frozen);
    }
}
