using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// A reused run adopts the sealed <c>preparationId</c> of the build it attaches to, because the
/// per-question scope hashes derive from it. That identity must not also become the run's own
/// identity: the report path is keyed on it, so a reused run would overwrite the accepted report of
/// the very cold build it reused - destroying the evidence that justified reusing it.
/// </summary>
public sealed class LongMemEvalReusedRunIdentityTests
{
    private const string PreparationId = "longmemeval-prepared-20260808T124308Z";

    private static readonly DateTimeOffset Now =
        new(2026, 8, 8, 16, 30, 15, TimeSpan.Zero);

    [Fact]
    public void AReusedRunGetsItsOwnIdentitySoItCannotOverwriteTheBuildItReused()
    {
        var runId = LongMemEvalPreparedPairProgram.ResolveRunId(PreparationId, reusing: true, Now);

        runId.Should().NotBe(PreparationId);
        // Still traceable back to the build it measured.
        runId.Should().StartWith(PreparationId).And.Contain("reuse");
    }

    [Fact]
    public void TwoReusedRunsOfTheSameBuildDoNotCollide()
    {
        var first = LongMemEvalPreparedPairProgram.ResolveRunId(PreparationId, reusing: true, Now);
        var second = LongMemEvalPreparedPairProgram.ResolveRunId(
            PreparationId, reusing: true, Now.AddSeconds(1));

        first.Should().NotBe(second);
    }

    [Fact]
    public void AColdRunKeepsThePreparationIdAsItsRunId()
    {
        // The existing artifact layout for cold builds is unchanged.
        LongMemEvalPreparedPairProgram.ResolveRunId(PreparationId, reusing: false, Now)
            .Should().Be(PreparationId);
    }
}
