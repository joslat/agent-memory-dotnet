using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// 25.7. A "noise band" may only pool runs that scored the <b>same number</b> of questions.
/// </summary>
/// <remarks>
/// <para>
/// The calculator's own comment said a differing denominator makes the comparison "between different
/// questions" — and the code only ever checked that accuracy was non-null. It pooled mismatched
/// denominators and then labelled the result with the <i>first</i> run's count.
/// </para>
/// <para>
/// This was invisible until the instrument was wired into a verb for the first time. Two accepted
/// 50-question runs had sampled 23 and 25 semantic questions; the tool reported a <b>±17.4 point noise
/// band</b> for semantic memory that was mostly the difference between two different question sets.
/// That number would have been published as this system's per-type measurement error.
/// </para>
/// </remarks>
public sealed class TypedNoiseFloorDenominatorTests
{
    private static LongMemEvalTypedAccuracy Row(string type, int questions, int correct) =>
        new(type, questions, correct, ExtractionFailures: 0, RetrievalFailures: 0, Unattributable: 0);

    [Fact]
    public void RunsWithDifferentDenominatorsAreNotPooledIntoABand()
    {
        // Red before the fix: this returned Runs=2 and a 35-point range, from 15/23 against 25/25 --
        // two different question sets reported as one configuration's variance.
        var floors = LongMemEvalTypedNoiseFloorCalculator.Measure(
        [
            [Row("semantic", questions: 23, correct: 15)],
            [Row("semantic", questions: 25, correct: 25)],
        ]);

        var semantic = floors.Should().ContainSingle().Subject;
        semantic.Runs.Should().Be(1, "only one run per denominator, so there is nothing to compare");
        semantic.StandardDeviation.Should().BeNull();
        semantic.RangePoints.Should().Be(0);
        semantic.Separates(50).Should().BeFalse("a single run separates nothing, however large the gap");
    }

    [Fact]
    public void MatchingDenominatorsStillMeasureARealSpread()
    {
        // The capability must survive the guard: same denominator, genuinely different results.
        var floors = LongMemEvalTypedNoiseFloorCalculator.Measure(
        [
            [Row("temporal", questions: 20, correct: 14)],
            [Row("temporal", questions: 20, correct: 18)],
        ]);

        var temporal = floors.Should().ContainSingle().Subject;
        temporal.Runs.Should().Be(2);
        temporal.Questions.Should().Be(20);
        temporal.RangePoints.Should().BeApproximately(20, 0.001);
        temporal.Separates(25).Should().BeTrue();
        temporal.Separates(15).Should().BeFalse();
    }

    [Fact]
    public void TheLargestComparableCohortWins()
    {
        // Three runs, two of which agree. The pair is the measurable thing; the odd one out must not
        // widen the band, and must not become the reported denominator either.
        var floors = LongMemEvalTypedNoiseFloorCalculator.Measure(
        [
            [Row("semantic", questions: 25, correct: 25)],
            [Row("semantic", questions: 25, correct: 20)],
            [Row("semantic", questions: 9, correct: 1)],
        ]);

        var semantic = floors.Should().ContainSingle().Subject;
        semantic.Runs.Should().Be(2);
        semantic.Questions.Should().Be(25, "the reported denominator must be the cohort's, not the first row's");
        semantic.RangePoints.Should().BeApproximately(20, 0.001);
    }

    [Fact]
    public void ReportedQuestionCountBelongsToTheCohortNotTheFirstRun()
    {
        // The mislabelling half of the defect, isolated. Ordering the mismatched run first used to
        // stamp its denominator onto a band measured from the others.
        var floors = LongMemEvalTypedNoiseFloorCalculator.Measure(
        [
            [Row("episodic", questions: 6, correct: 3)],
            [Row("episodic", questions: 4, correct: 2)],
            [Row("episodic", questions: 4, correct: 4)],
        ]);

        floors.Should().ContainSingle().Which.Questions.Should().Be(4);
    }
}
