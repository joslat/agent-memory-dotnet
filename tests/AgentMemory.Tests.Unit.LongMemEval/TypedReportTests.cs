using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// The integrity rules, enforced in the renderer rather than left to whoever writes the prose.
/// </summary>
/// <remarks>
/// The failure mode guarded against is a number that travels without its context. Assume every caveat
/// is stripped and the bare figure is quoted — so the figure itself has to be safe.
/// </remarks>
public sealed class TypedReportTests
{
    private static readonly LongMemEvalTypedAccuracy Episodic = new("episodic", 6, 4, 0, 2, 0);
    private static readonly LongMemEvalTypedAccuracy Semantic = new("semantic", 23, 20, 0, 2, 1);

    [Fact]
    public void ASingleRunIsLabelledAsHavingNoBand()
    {
        // The most dangerous artefact this project could produce: a per-type table from ONE run that
        // reads as though it supports comparison.
        var report = LongMemEvalTypedReport.Render([Episodic, Semantic]);

        report.Should().Contain("not measured");
        report.Should().Contain("No band has been measured");
        report.Should().Contain("none of them supports a comparison");
    }

    [Fact]
    public void EveryRowCarriesItsQuestionCountAndWhatOneQuestionIsWorth()
    {
        var report = LongMemEvalTypedReport.Render([Episodic]);

        report.Should().Contain("| episodic | 6 |");
        report.Should().Contain("16.7 pts", "six questions is 16.7 accuracy points each, and a reader must see that");
    }

    [Fact]
    public void AnAblationInsideTheBandIsPrintedAsNoResult()
    {
        // Not as a small win. A component that does not earn its tokens should be found here.
        var floors = LongMemEvalTypedNoiseFloorCalculator.Measure(new[]
        {
            new[] { new LongMemEvalTypedAccuracy("episodic", 6, 4, 0, 2, 0) },
            new[] { new LongMemEvalTypedAccuracy("episodic", 6, 2, 0, 4, 0) },
        });
        var ablation = new LongMemEvalAblationResult(
            LongMemEvalCapability.Episodic,
            [new LongMemEvalQuestionFlip("e1", "episodic", true, false)],
            6);

        var report = LongMemEvalTypedReport.Render([Episodic], floors, [ablation]);

        report.Should().Contain("no result — inside the band");
    }

    [Fact]
    public void ChurnIsCalledOutEvenWhenTheNetLooksFine()
    {
        var ablation = new LongMemEvalAblationResult(
            LongMemEvalCapability.Episodic,
            [
                new LongMemEvalQuestionFlip("e1", "episodic", true, false),
                new LongMemEvalQuestionFlip("e2", "episodic", true, false),
                new LongMemEvalQuestionFlip("e3", "episodic", false, true),
            ],
            6);

        var report = LongMemEvalTypedReport.Render([Episodic], null, [ablation]);

        report.Should().Contain("Churn:");
        report.Should().Contain("moving answers");
    }

    [Fact]
    public void TheUnreachableTypesAreAlwaysStated()
    {
        // A missing row must never read as a zero. Procedural is absent because the dataset cannot
        // reach it, not because we scored badly.
        var report = LongMemEvalTypedReport.Render([Semantic]);

        report.Should().Contain("Procedural memory is unreachable here at any sample size");
        report.Should().Contain("a missing row means unmeasured, never zero");
    }

    [Fact]
    public void TheMappingRevisionIsPrintedWhenSupplied()
    {
        var report = LongMemEvalTypedReport.Render([Semantic], mappingRevision: "2026-08-12");

        report.Should().Contain("2026-08-12");
        report.Should().Contain("The grouping is an opinion");
    }

    [Fact]
    public void AMeasuredBandReplacesTheNotMeasuredLabel()
    {
        var floors = LongMemEvalTypedNoiseFloorCalculator.Measure(new[]
        {
            new[] { new LongMemEvalTypedAccuracy("episodic", 6, 4, 0, 2, 0) },
            new[] { new LongMemEvalTypedAccuracy("episodic", 6, 3, 0, 3, 0) },
        });

        var report = LongMemEvalTypedReport.Render([Episodic], floors);

        report.Should().Contain("±");
        // The BAND CELL specifically, not the prose: the closing section legitimately says a missing
        // row means "unmeasured", and a broader assertion here failed against correct output.
        report.Should().NotContain("**not measured**");
        report.Should().NotContain("No band has been measured");
    }
}
