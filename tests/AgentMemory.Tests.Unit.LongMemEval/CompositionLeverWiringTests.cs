using AgentMemory.Abstractions.Options;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// The three retrieval levers the C-D family run proved were unreachable from this verb.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this closes.</b> Five verticals (215 + 80 questions, ~35 hours) were measured with the
/// engine's composition machinery hard off, because <c>ExpandFactsByPredicate</c> and
/// <c>ResolveQueryRelations</c> were set only by <c>LongMemEvalPreparedPairProgram</c> and
/// <c>RecallFanOutOptions.Enabled</c> was set by nothing under <c>tools/</c> at all. The adapter's
/// own comments name the exact shapes that then scored zero: "a relation returned whole, for the
/// aggregation questions top-K structurally cannot answer".
/// </para>
/// <para>
/// A flag that parses is not a flag that arrives. Each test below follows one lever from the command
/// line to the object the engine actually reads, because the five instances of reachable-but-not-fed
/// this project has now found were every one of them a flag that looked configured.
/// </para>
/// </remarks>
public sealed class CompositionLeverWiringTests
{
    private static TypedMemEvalProgram.TypedMemEvalRunOptions Parse(params string[] extra) =>
        TypedMemEvalProgram.Parse(["--typedmemeval", "arithmetic", .. extra]);

    [Theory]
    [InlineData("--expand-facts")]
    [InlineData("--resolve-query-relations")]
    [InlineData("--recall-fan-out")]
    public void EachLeverIsAKnownOption(string flag) =>
        TypedMemEvalProgram.KnownOptions.Should().Contain(flag,
            "an unlisted option is rejected by the validator, so a mistyped run would spend money "
            + "on the wrong arm -- which is what the validator exists to prevent");

    [Fact]
    public void TheFlagsReachTheOptionsRecord()
    {
        var on = Parse("--expand-facts", "--resolve-query-relations", "--recall-fan-out");

        on.ExpandFactsByPredicate.Should().BeTrue();
        on.ResolveQueryRelations.Should().BeTrue();
        on.RecallFanOut.Should().BeTrue();
    }

    [Fact]
    public void TheDefaultArmLeavesAllThreeOff()
    {
        var off = Parse();

        off.ExpandFactsByPredicate.Should().BeFalse();
        off.ResolveQueryRelations.Should().BeFalse();
        off.RecallFanOut.Should().BeFalse();
        off.Arm.IsDefault.Should().BeTrue(
            "every sealed measurement so far was taken on this path and must stay byte-identical");
    }

    /// <summary>
    /// An ON artifact must never be mistakable for the default arm on disk.
    /// </summary>
    /// <remarks>
    /// The arm is stamped into the FILENAME. Two runs differing only by a lever that produced the
    /// same token would be indistinguishable once the shell log is gone -- the defect
    /// <see cref="TypedMemEvalArm"/> was created to close, re-asserted for the new levers.
    /// </remarks>
    [Theory]
    [InlineData("--expand-facts", "expand")]
    [InlineData("--resolve-query-relations", "qrel")]
    [InlineData("--recall-fan-out", "fanout")]
    public void EachLeverNamesItselfInTheArmToken(string flag, string token)
    {
        var arm = Parse(flag).Arm;

        arm.IsDefault.Should().BeFalse();
        arm.FileToken().Should().Be(token);
        arm.Describe().Should().Contain("=True");
    }

    [Fact]
    public void TheCombinedArmNamesEveryLeverItHadOn()
    {
        var arm = Parse("--expand-facts", "--resolve-query-relations", "--recall-fan-out").Arm;

        arm.FileToken().Should().Be("expand-qrel-fanout");
    }

    /// <summary>
    /// The off-state of fan-out must be the shipped off-state, asserted rather than assumed.
    /// </summary>
    [Fact]
    public void FanOutShipsOff() =>
        new MemoryOptions().FanOut.Enabled.Should().BeFalse(
            "the five-vertical baseline was taken on this path");
}

/// <summary>
/// The derived-fact budget must be visible in the arm token — including its VALUE.
/// </summary>
/// <remarks>
/// Declared in the pre-registration before the run rather than discovered after it: without this,
/// the derived-budget arm and row 61's arm (same accountant, same expansion, no read side) produce
/// an identical token and their artifacts are indistinguishable on disk. That is precisely the
/// defect <see cref="TypedMemEvalArm"/> exists to close, and 0 versus 10 are different arms with
/// different predictions — exclude versus own-budget.
/// </remarks>
public sealed class DerivedBudgetArmTokenTests
{
    private static TypedMemEvalProgram.TypedMemEvalRunOptions Parse(params string[] extra) =>
        TypedMemEvalProgram.Parse(["--typedmemeval", "arithmetic", .. extra]);

    [Fact]
    public void TheBudgetIsAKnownOption() =>
        TypedMemEvalProgram.KnownOptions.Should().Contain("--max-derived-facts");

    [Theory]
    [InlineData("0", "derived0")]
    [InlineData("10", "derived10")]
    public void TheTOKENCarriesTheValueNotJustThatItWasSet(string value, string token)
    {
        var arm = Parse("--max-derived-facts", value).Arm;

        arm.IsDefault.Should().BeFalse();
        arm.FileToken().Should().Be(token);
    }

    [Fact]
    public void TwoDifferentBudgetsAreDifferentArmsOnDisk() =>
        Parse("--max-derived-facts", "0").Arm.FileToken()
            .Should().NotBe(Parse("--max-derived-facts", "10").Arm.FileToken(),
                "exclude and own-budget make opposite predictions and must never share a filename");

    [Fact]
    public void UnsetLeavesTheArmDefault() =>
        Parse().Arm.IsDefault.Should().BeTrue(
            "null is the pre-existing path and every sealed measurement was taken on it");

    /// <summary>Zero must not read as "unset" — it is an explicit, different arm.</summary>
    [Fact]
    public void ZeroIsNotTheSameAsUnset()
    {
        Parse("--max-derived-facts", "0").Arm.IsDefault.Should().BeFalse();
        Parse().Arm.FileToken().Should().Be("default");
    }

    [Fact]
    public void ANegativeBudgetIsRejected()
    {
        var act = () => Parse("--max-derived-facts", "-1");

        act.Should().Throw<ArgumentException>(
            "a negative budget is not a narrower arm, it is a typo that would otherwise run");
    }
}
