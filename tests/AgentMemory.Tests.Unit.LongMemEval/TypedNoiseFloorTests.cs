using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// The per-type band that has to exist before any per-type number is published.
/// </summary>
/// <remarks>
/// A per-type subset is small — episodic is 6 of 50 questions, i.e. <b>16.7 accuracy points per
/// question</b> — and the whole-run band (~±9 points at n=50) does not transfer to it. Quoting a
/// per-type figure without its own band is how a table invites exactly the comparison it cannot
/// support.
/// </remarks>
public sealed class TypedNoiseFloorTests
{
    private static LongMemEvalTypedAccuracy Row(string type, int questions, int correct) =>
        new(type, questions, correct, 0, questions - correct, 0);

    [Fact]
    public void SpreadIsMeasuredAcrossRepeatsOfTheSameArm()
    {
        var runs = new[]
        {
            new[] { Row("episodic", 6, 4) },   // 66.7%
            new[] { Row("episodic", 6, 3) },   // 50.0%
            new[] { Row("episodic", 6, 5) },   // 83.3%
        };

        var floor = LongMemEvalTypedNoiseFloorCalculator.Measure(runs).Single();

        floor.Runs.Should().Be(3);
        floor.Questions.Should().Be(6);
        floor.RangePoints.Should().BeApproximately(33.3, 0.1);
        floor.PointsPerQuestion.Should().BeApproximately(16.67, 0.01);
        floor.StandardDeviation.Should().NotBeNull();
    }

    [Fact]
    public void ADifferenceInsideTheObservedSpreadDoesNotSeparate()
    {
        // The load-bearing guard. If one configuration already varied by 33 points against ITSELF, a
        // 20-point difference against another configuration is not a result -- and reporting it as one
        // is precisely how this category produces numbers that dissolve under scrutiny.
        var runs = new[]
        {
            new[] { Row("episodic", 6, 4) },
            new[] { Row("episodic", 6, 3) },
            new[] { Row("episodic", 6, 5) },
        };

        var floor = LongMemEvalTypedNoiseFloorCalculator.Measure(runs).Single();

        floor.Separates(20.0).Should().BeFalse("20 points is inside a 33-point self-spread");
        floor.Separates(40.0).Should().BeTrue();
    }

    [Fact]
    public void ASingleRunSeparatesNothing()
    {
        // One run yields no spread, so nothing is separable. That is the correct answer, not a missing
        // feature -- and it is why a first-ever per-type table cannot carry a comparative claim.
        var floor = LongMemEvalTypedNoiseFloorCalculator
            .Measure(new[] { new[] { Row("semantic", 23, 20) } }).Single();

        floor.Runs.Should().Be(1);
        floor.StandardDeviation.Should().BeNull();
        floor.Separates(0.1).Should().BeFalse();
        floor.Separates(99.0).Should().BeFalse("with one run there is no observed variation to beat");
    }

    [Fact]
    public void PerfectlyStableRepeatsReportZeroSpread()
    {
        // What a deterministic extractor would look like. Worth being able to SEE, because it is the
        // whole prize of a temperature-0 deployment: a zero band means one build per arm suffices.
        var runs = Enumerable.Repeat(new[] { Row("temporal", 21, 21) }, 3).ToArray();

        var floor = LongMemEvalTypedNoiseFloorCalculator.Measure(runs).Single();

        floor.RangePoints.Should().Be(0);
        floor.StandardDeviation.Should().Be(0);
        floor.Separates(0.5).Should().BeTrue("with no observed variation, any real difference separates");
    }

    [Fact]
    public void TypesAreMeasuredIndependently()
    {
        var runs = new[]
        {
            new[] { Row("semantic", 23, 20), Row("episodic", 6, 4) },
            new[] { Row("semantic", 23, 20), Row("episodic", 6, 2) },
        };

        var floors = LongMemEvalTypedNoiseFloorCalculator.Measure(runs);

        floors.Single(f => f.MemoryType == "semantic").RangePoints.Should().Be(0);
        floors.Single(f => f.MemoryType == "episodic").RangePoints.Should().BeApproximately(33.3, 0.1);
    }

    [Fact]
    public void NoRunsYieldsNoBands()
    {
        LongMemEvalTypedNoiseFloorCalculator.Measure([]).Should().BeEmpty();
    }
}
