using AgentMemory.Cli.Perf;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Cli;

public sealed class QualityGateTests
{
    [Fact]
    public void Evaluate_ExactBaseline_PassesAtZeroTolerance()
    {
        var result = QualityGate.Evaluate(Baseline(), Retrieval(), Extraction(), "quality.json");

        result.Passed.Should().BeTrue();
        result.Tolerance.Should().Be(0);
        result.Violations.Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_RetrievalBelowBaseline_Fails()
    {
        var result = QualityGate.Evaluate(
            Baseline(),
            Retrieval(recallAtK: 18d / 19d, mrr: 18d / 19d),
            Extraction(),
            "quality.json");

        result.Passed.Should().BeFalse();
        result.Violations.Should()
            .Contain(v => v.Contains("retrieval.recallAtK", StringComparison.Ordinal))
            .And.Contain(v => v.Contains("retrieval.mrr", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_ForbiddenRetrieval_FailsEvenWhenScoresMeetBaseline()
    {
        var result = QualityGate.Evaluate(
            Baseline(),
            Retrieval(casesWithViolations: 1),
            Extraction(),
            "quality.json");

        result.Passed.Should().BeFalse();
        result.Violations.Should()
            .ContainSingle(v => v.Contains("forbidden retrieval", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_ExtractionBelowBaseline_Fails()
    {
        var result = QualityGate.Evaluate(
            Baseline(),
            Retrieval(),
            Extraction(factRecall: 0.95),
            "quality.json");

        result.Passed.Should().BeFalse();
        result.Violations.Should()
            .ContainSingle(v => v.Contains("extraction.factRecall", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_ExtractionFalsePositiveIncrease_Fails()
    {
        var result = QualityGate.Evaluate(
            Baseline(),
            Retrieval(),
            Extraction(falsePositives: 1),
            "quality.json");

        result.Passed.Should().BeFalse();
        result.Violations.Should()
            .Contain(v => v.Contains("extraction.falsePositiveRate", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(18, 20, 6)]
    [InlineData(19, 19, 6)]
    [InlineData(19, 20, 5)]
    public void Evaluate_FixtureCaseCountChanged_Fails(
        int retrievalCases,
        int extractionCases,
        int expectNothingCases)
    {
        var result = QualityGate.Evaluate(
            Baseline(),
            Retrieval(cases: retrievalCases),
            Extraction(cases: extractionCases, expectNothingCases: expectNothingCases),
            "quality.json");

        result.Passed.Should().BeFalse();
        result.Violations.Should().NotBeEmpty();
    }

    [Fact]
    public void Evaluate_RejectsRetrievalBaselineThatClaimsSemanticQuality()
    {
        var baseline = Baseline();
        baseline = baseline with
        {
            Retrieval = baseline.Retrieval with
            {
                Measurement = "semantic-retrieval",
                SemanticQualityClaim = true
            }
        };

        var act = () => QualityGate.Evaluate(baseline, Retrieval(), Extraction());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*deterministic-plumbing*");
    }

    private static QualityBaseline Baseline() => new(
        SchemaVersion: 1,
        Tolerance: 0,
        Retrieval: new RetrievalQualityBaseline(
            Measurement: "deterministic-plumbing",
            SemanticQualityClaim: false,
            RecallAtK: 1,
            Mrr: 1,
            Cases: 19,
            MaxCasesWithViolations: 0),
        Extraction: new ExtractionQualityBaseline(
            EntityPrecision: 1,
            EntityRecall: 1,
            FactPrecision: 1,
            FactRecall: 1,
            PreferencePrecision: 1,
            PreferenceRecall: 1,
            Cases: 20,
            ExpectNothingCases: 6,
            MaxFalsePositiveRate: 0));

    private static QualityResult Retrieval(
        double recallAtK = 1,
        double mrr = 1,
        int cases = 19,
        int casesWithViolations = 0) => new(
            RecallAtK: recallAtK,
            Mrr: mrr,
            Cases: cases,
            CasesWithViolations: casesWithViolations,
            RecallByCategory: new Dictionary<string, double>(),
            CaseResults: []);

    private static ExtractionQualityResult Extraction(
        double factRecall = 1,
        int cases = 20,
        int expectNothingCases = 6,
        int falsePositives = 0) => new(
            EntityPrecision: 1,
            EntityRecall: 1,
            FactPrecision: 1,
            FactRecall: factRecall,
            PreferencePrecision: 1,
            PreferenceRecall: 1,
            Cases: cases,
            ExpectNothingCases: expectNothingCases,
            FalsePositives: falsePositives,
            CaseResults: []);
}
