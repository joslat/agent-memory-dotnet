using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// The fact-grained instrument, tested on the shapes that motivated it.
/// </summary>
/// <remarks>
/// Row 56 pre-registered a session-grained metric to confirm a fact-grained mechanism and it nearly
/// missed the effect. The replacement is only worth having if it can fail — so these assert the
/// boundaries that would make it lie: normalisation across notations, null rather than zero when
/// nothing is measurable, and a real miss actually reading as a miss.
/// </remarks>
public sealed class GoldValueCoverageTests
{
    [Theory]
    [InlineData("$1,113.71 in total.", "1113.71")]
    [InlineData("costs 371.41 today", "371.41")]
    [InlineData("$420", "420")]
    public void AmountsNormaliseToOneValue(string text, string expected) =>
        LongMemEvalGoldValueCoverage.Extract(text).Should().Contain(expected);

    /// <summary>
    /// The comparison only means anything if the two notations collapse to the same value.
    /// </summary>
    /// <remarks>
    /// Gold writes "$1,113.71"; an extracted fact usually writes "1113.71". Comparing raw strings
    /// would report a miss for a value that is present, biasing the metric toward "retrieval
    /// failed" — the very conclusion it exists to test.
    /// </remarks>
    [Fact]
    public void TheGoldNotationAndTheFactNotationMatch()
    {
        var c = LongMemEvalGoldValueCoverage.Measure(
            "$1,113.71 in total on the Dunstan Yard roofline.",
            ["Payment | has amount | 1113.71"]);

        c.Should().NotBeNull();
        c!.Value.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void OneDigitNumbersAreNotValues() =>
        LongMemEvalGoldValueCoverage.Extract("step 3 of the routine")
            .Should().BeEmpty("single digits match ordinary prose and would inflate every question");

    /// <summary>Unmeasurable must stay distinct from zero.</summary>
    /// <remarks>
    /// An ordering question ("the Ennisk pass runs first") carries no amount and no quantity. Scored
    /// 0.0 it would read as a total retrieval failure and drag the mean toward a conclusion the data
    /// never supported — the constant-column failure this project has hit three times.
    /// </remarks>
    [Fact]
    public void GoldWithNoRecognisedValueIsNullNotZero() =>
        LongMemEvalGoldValueCoverage.Measure(
            "The Ennisk pass runs before the Kelvaryn tally.", ["a | b | c"])
            .Should().BeNull();

    [Fact]
    public void AMissingValueReadsAsAMiss()
    {
        var c = LongMemEvalGoldValueCoverage.Measure(
            "$155.31 -- the staircase cost that much more.",
            ["Payment | has amount | 399.56", "Payment | has amount | 282.45"]);

        c.Should().NotBeNull();
        c!.Value.IsComplete.Should().BeFalse();
        c.Value.PresentValues.Should().Be(0);
    }

    /// <summary>The partial case — the one the session metric could not see.</summary>
    [Fact]
    public void PartialCoverageIsReportedAsPartial()
    {
        var c = LongMemEvalGoldValueCoverage.Measure(
            "371.41 and 354.06 and 388.24",
            ["f | amount | 371.41", "f | amount | 354.06"]);

        c!.Value.RequiredValues.Should().Be(3);
        c.Value.PresentValues.Should().Be(2);
        c.Value.Fraction.Should().BeApproximately(2.0 / 3.0, 1e-9);
    }

    [Fact]
    public void NoFactsMeansNoCoverageRatherThanACrash() =>
        LongMemEvalGoldValueCoverage.Measure("$12.50", [])!.Value.PresentValues.Should().Be(0);
}
