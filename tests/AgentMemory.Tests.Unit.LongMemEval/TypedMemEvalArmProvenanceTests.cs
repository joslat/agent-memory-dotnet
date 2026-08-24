using AgentEval.Memory.External.TypedMemEval;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// A run artifact must name its own arm (validity requirement, 2026-08-22).
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect.</b> Two Arithmetic runs differing only by a flag produced artifacts identical in
/// every visible way — same vertical, same seed, same shape. Telling the control from the treatment
/// required a shell log, and by the time anyone asked, that log was gone. An artifact that cannot
/// name its own arm is not evidence; it is a number with its provenance kept somewhere else.
/// </para>
/// <para>
/// These assert the token, not the whole filename, because the filename also carries a timestamp.
/// The claim under test is that <b>all four arms are distinguishable from each other</b>, which is
/// exactly what failed before.
/// </para>
/// </remarks>
public sealed class TypedMemEvalArmProvenanceTests
{
    [Fact]
    public void AllLeversOffIsNamedDefault_NotLeftBlank()
    {
        // Blank would be indistinguishable from a pre-provenance artifact, so the default arm has to
        // assert itself rather than be inferred from an absence.
        TypedMemEvalArm.Default.FileToken().Should().Be("default");
        TypedMemEvalArm.Default.IsDefault.Should().BeTrue();
    }

    [Theory]
    [InlineData(false, false, false, false, "default")]
    [InlineData(true, false, false, false, "wm")]
    [InlineData(false, true, false, false, "arith")]
    [InlineData(false, false, true, false, "rescue")]
    [InlineData(false, false, false, true, "factwt")]
    public void EachLeverNamesItselfInTheToken(
        bool workingMemory, bool arithmetic, bool rescue, bool factWeighted, string expected)
    {
        // Named, not positional: inserting SupersedeReplacedFacts ahead of FactWeightedBudget
        // silently re-slotted these and the token tests were the only thing that noticed.
        var arm = new TypedMemEvalArm(
            new PhaseThirtyFeatures(workingMemory, arithmetic),
            RescueShortOwnerResults: rescue,
            FactWeightedBudget: factWeighted);

        arm.FileToken().Should().Be(expected);
    }

    [Fact]
    public void TheFourArmsThisTrackActuallyRanAreAllDistinguishable()
    {
        // The concrete failure: Arm A (control), Arm B (rescue) and the fact-weighted arm are the same
        // vertical and the same seed. Before this, all three produced the same filename shape.
        var control = TypedMemEvalArm.Default;
        var rescue = new TypedMemEvalArm(PhaseThirtyFeatures.AllOff, RescueShortOwnerResults: true);
        var factWeighted = new TypedMemEvalArm(PhaseThirtyFeatures.AllOff, FactWeightedBudget: true);
        var arithmeticMemory = new TypedMemEvalArm(new PhaseThirtyFeatures(false, true));

        var tokens = new[]
        {
            control.FileToken(), rescue.FileToken(),
            factWeighted.FileToken(), arithmeticMemory.FileToken(),
        };

        tokens.Should().OnlyHaveUniqueItems(
            "two arms sharing a token is the whole defect this closes");
    }

    [Fact]
    public void CombinedLeversAllAppear()
    {
        var arm = new TypedMemEvalArm(
            new PhaseThirtyFeatures(true, true),
            RescueShortOwnerResults: true,
            SupersedeReplacedFacts: true,
            FactWeightedBudget: true);

        var token = arm.FileToken();

        token.Should().Contain("wm").And.Contain("arith")
            .And.Contain("rescue").And.Contain("supersede").And.Contain("factwt");
    }

    [Fact]
    public void TheTokenIsFilenameSafe()
    {
        // PhaseThirtyFeatures.Describe() returns "phase30:none", and a colon is not legal in a Windows
        // filename. A token needing sanitisation at each use is one that will eventually be sanitised
        // differently at one of them.
        var invalid = Path.GetInvalidFileNameChars();

        foreach (var arm in new[]
        {
            TypedMemEvalArm.Default,
            new TypedMemEvalArm(new PhaseThirtyFeatures(true, true), true, true),
        })
        {
            arm.FileToken().IndexOfAny(invalid).Should().Be(-1,
                "the token goes straight into a path");
        }
    }

    [Fact]
    public void DescribeNamesEveryLeverIncludingTheOnesThatAreOff()
    {
        // Both states are recorded. "rescue=false" is a measurement of the control; a describe string
        // that only listed what was ON could not tell "the lever was off" from "this build predates
        // the lever".
        var describe = TypedMemEvalArm.Default.Describe();

        describe.Should().Contain("rescue-short-owner-results=False");
        describe.Should().Contain("fact-weighted-budget=False");
        describe.Should().Contain("phase30:none");
    }

    [Fact]
    public void PhaseThirtyDescribeIsNowReachable()
    {
        // It was dead code whose own docstring said "run provenance and report file names" -- the
        // sixteenth ship-but-unreachable instance in this repository, inside the very type built to
        // make arms legible. Composing it here is what makes it reached.
        var arm = new TypedMemEvalArm(new PhaseThirtyFeatures(true, false));

        arm.Describe().Should().Contain(new PhaseThirtyFeatures(true, false).Describe());
    }

    [Fact]
    public void TheArmIsDerivedFromTheFlagsSoItCannotDrift()
    {
        // A stored token that disagreed with the options that produced it would be worse than none.
        var options = new TypedMemEvalProgram.TypedMemEvalRunOptions(
            [TypedMemEvalVertical.Arithmetic], 50, 20260821, null, 1, false, false,
            new PhaseThirtyFeatures(false, true), RescueShortOwnerResults: true,
            SupersedeReplacedFacts: false,
            EvidenceDetail: LongMemEvalEvidenceDetail.Identifiers,
            FactWeightedBudget: true);

        var arm = options.Arm;

        arm.Phase30.ArithmeticMemory.Should().BeTrue();
        arm.RescueShortOwnerResults.Should().BeTrue();
        arm.FactWeightedBudget.Should().BeTrue();
        arm.IsDefault.Should().BeFalse();
    }
}
