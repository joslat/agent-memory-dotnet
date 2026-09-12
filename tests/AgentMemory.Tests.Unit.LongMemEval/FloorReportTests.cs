using AgentEval.Evals.Meta;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// Chance floors, validated against a cell whose answer this project already knows.
/// </summary>
/// <remarks>
/// <para>
/// Temporal's <c>occurrence-order</c> read <b>11/20 = 55%</b> against a 0.50 floor — within noise of
/// a coin, and nothing in the artifact said so. The floor had to be remembered from correspondence,
/// which is how a cell carrying no information nearly got reported as a result.
/// </para>
/// <para>
/// These assert the instrument in BOTH directions against known cells, because five instruments in
/// this wave passed a one-sided check and failed calibration: a metric that cannot distinguish the
/// coin-flip cell from the perfect cell would be worse than none.
/// </para>
/// </remarks>
public sealed class FloorReportTests
{
    private const string Arm = "expand-qrel";

    private static IReadOnlyList<Observation> Cell(int successes, int trials) =>
        Enumerable.Range(0, trials)
            .Select(i => new Observation($"q{i}", Arm, i < successes ? 1.0 : 0.0, MeasurementState.Measured))
            .ToArray();

    /// <summary>The real cell: 11/20 against a coin is NOT a result.</summary>
    [Fact]
    public void TheOccurrenceOrderCellDoesNotClearACoin()
    {
        var floor = TypedMemEvalFloorReport.FloorFor("occurrence-order");
        var comparison = FloorComparison.Compute(Cell(11, 20), Arm, floor);

        floor.Value.Should().BeApproximately(0.50, 1e-9, "a binary ordering question is a coin");
        comparison.AboveFloor.Should().BeFalse(
            "11/20 = 55% is within noise of 50%, and this is the cell that was nearly reported as "
            + "a temporal capability");
    }

    /// <summary>The other direction: the pre-de-leaking 20/20 clearly does clear it.</summary>
    /// <remarks>
    /// Without this the test above would pass for an instrument that always says "not above floor".
    /// </remarks>
    [Fact]
    public void APerfectCellDoesClearTheSameFloor()
    {
        var comparison = FloorComparison.Compute(
            Cell(20, 20), Arm, TypedMemEvalFloorReport.FloorFor("occurrence-order"));

        comparison.AboveFloor.Should().BeTrue(
            "20/20 against a coin is unambiguous — an instrument that could not see this would be "
            + "measuring nothing");
    }

    /// <summary>
    /// A shape whose answer space we cannot derive says so, and does NOT get a floor of zero.
    /// </summary>
    /// <remarks>
    /// A zero floor reads as "any score beats chance", which converts an uninformative cell into an
    /// apparent result — the exact false confidence this instrument exists to prevent. AgentEval put
    /// the distinction in the type; the point of this test is that we use it rather than default.
    /// </remarks>
    [Theory]
    [InlineData("sum")]
    [InlineData("delta")]
    [InlineData("alias-then-count")]
    [InlineData("step-order")]
    public void AnUnderivableFloorIsNotDerivableRatherThanZero(string shape)
    {
        var floor = TypedMemEvalFloorReport.FloorFor(shape);

        floor.State.Should().Be(FloorState.NotDerivable);
        floor.Derivation.Should().NotBeNullOrWhiteSpace(
            "the reason travels with the refusal, so the next reader knows it was considered");
    }

    /// <summary>
    /// Infrastructure failures are NOT MEASURED, not wrong answers.
    /// </summary>
    /// <remarks>
    /// Two paid runs in this wave died mid-flight and their artifacts reported 7/39. Mapping agent
    /// failures to NotMeasured makes the census say VOID instead of leaving a reader to notice that
    /// a contiguous block of questions never ran.
    /// </remarks>
    [Fact]
    public void ADeadRunCensusesAsUnmeasuredRatherThanScoringLow()
    {
        var measured = Cell(7, 39).ToList();
        // The 11 questions that never ran when the store died.
        measured.AddRange(Enumerable.Range(0, 11)
            .Select(i => new Observation($"dead{i}", Arm, 0.0, MeasurementState.NotMeasured)));

        var census = FloorComparison
            .Compute(measured, Arm, TypedMemEvalFloorReport.FloorFor("sum")).Census;

        census.Total.Should().Be(50);
        census.NotMeasured.Should().Be(11);
        census.Measured.Should().Be(39, "the eleven that never ran are absent, not incorrect");
    }
}
