using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// Ablating one capability and reporting <b>which questions moved</b>, not just an aggregate.
/// </summary>
/// <remarks>
/// An aggregate hides the two things worth knowing: which questions a capability rescued, and whether
/// it broke any that previously worked. A capability that gains three and loses three reports as
/// neutral and is not — it is churn, and churn is the signature of a change moving noise rather than
/// adding signal.
/// </remarks>
public sealed class AblationTests
{
    private static readonly Dictionary<string, string> Types = new()
    {
        ["e1"] = "episodic", ["e2"] = "episodic", ["e3"] = "episodic",
        ["s1"] = "semantic", ["s2"] = "semantic",
    };

    [Fact]
    public void GainsAndLossesAreReportedSeparately()
    {
        var with = new Dictionary<string, bool> { ["e1"] = true, ["e2"] = false, ["s1"] = true };
        var without = new Dictionary<string, bool> { ["e1"] = false, ["e2"] = true, ["s1"] = true };

        var result = LongMemEvalAblation.Compare(
            LongMemEvalCapability.Episodic, with, without, Types);

        result.Gains.Should().ContainSingle().Which.QuestionId.Should().Be("e1");
        result.Losses.Should().ContainSingle().Which.QuestionId.Should().Be("e2");
        result.Net.Should().Be(0);
        result.QuestionsCompared.Should().Be(3);
    }

    [Fact]
    public void ChurnIsVisibleEvenThoughTheNetIsZero()
    {
        // The case an aggregate hides entirely: three rescued, three broken, net zero. Reporting only
        // the net would present a capability that is churning answers as one that does nothing.
        var with = new Dictionary<string, bool>
            { ["e1"] = true, ["e2"] = true, ["e3"] = true, ["s1"] = false, ["s2"] = false };
        var without = new Dictionary<string, bool>
            { ["e1"] = false, ["e2"] = false, ["e3"] = false, ["s1"] = true, ["s2"] = true };

        var result = LongMemEvalAblation.Compare(
            LongMemEvalCapability.Episodic, with, without, Types);

        result.Net.Should().Be(1);
        result.Gains.Should().HaveCount(3);
        result.Losses.Should().HaveCount(2);
        result.Flips.Should().HaveCount(5, "every moved question is listed, not just the balance");
    }

    [Fact]
    public void AQuestionJudgedInOnlyOneArmIsNotCompared()
    {
        // Including it would let an arm that simply answered FEWER questions look like a capability
        // effect -- an artefact that would be indistinguishable from a real gain in the aggregate.
        var with = new Dictionary<string, bool> { ["e1"] = true, ["e2"] = true };
        var without = new Dictionary<string, bool> { ["e1"] = false };

        var result = LongMemEvalAblation.Compare(
            LongMemEvalCapability.Episodic, with, without, Types);

        result.QuestionsCompared.Should().Be(1);
        result.Flips.Should().ContainSingle().Which.QuestionId.Should().Be("e1");
    }

    [Fact]
    public void ANegativeResultIsRepresentable()
    {
        // A capability can make things worse, and that must be reportable rather than clamped. Finding
        // it in-house is the whole point; the alternative is a competitor finding it.
        var with = new Dictionary<string, bool> { ["e1"] = false, ["e2"] = false };
        var without = new Dictionary<string, bool> { ["e1"] = true, ["e2"] = true };

        var result = LongMemEvalAblation.Compare(
            LongMemEvalCapability.Episodic, with, without, Types);

        result.Net.Should().Be(-2);
        result.NetPoints.Should().Be(-100);
    }

    [Fact]
    public void MovementInsideTheNoiseFloorIsNotAResult()
    {
        // 1 of 6 questions is 16.7 points. If episodic already varied by 33 points against ITSELF,
        // that movement is noise -- and a null result is publishable, not a failure.
        var with = new Dictionary<string, bool>
            { ["e1"] = true, ["e2"] = true, ["e3"] = true, ["s1"] = true, ["s2"] = true };
        var without = new Dictionary<string, bool>
            { ["e1"] = false, ["e2"] = true, ["e3"] = true, ["s1"] = true, ["s2"] = true };

        var result = LongMemEvalAblation.Compare(LongMemEvalCapability.Episodic, with, without, Types);
        var floors = LongMemEvalTypedNoiseFloorCalculator.Measure(new[]
        {
            new[] { new LongMemEvalTypedAccuracy("episodic", 6, 4, 0, 2, 0) },
            new[] { new LongMemEvalTypedAccuracy("episodic", 6, 2, 0, 4, 0) },
        });

        LongMemEvalAblation.SurvivesNoiseFloor(result, floors).Should().BeFalse();
    }

    [Fact]
    public void EveryCapabilityNamesTheOptionThatDisablesIt()
    {
        // A published ablation that cannot say which switch produced it is not reproducible.
        foreach (var capability in LongMemEvalCapability.All)
        {
            capability.Option.Should().NotBeNullOrWhiteSpace();
            capability.MemoryType.Should().NotBeNullOrWhiteSpace();
        }

        LongMemEvalCapability.Episodic.MemoryType.Should().Be("episodic");
        LongMemEvalCapability.Traces.MemoryType.Should().Be("procedural");
    }
}
