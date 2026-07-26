using System.Text.Json;
using AgentMemory.Cli.Commands;
using AgentMemory.Cli.Perf;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Cli;

public sealed class PerfBaselineGateTests
{
    [Fact]
    public void BaselineFromSummary_StoresPortableCountersAndQuality()
    {
        var baseline = PerfBaselineDocument.FromSummary(Report());

        baseline.Scenarios["PERF-R-04"].Counters["neo4j.queries"].Should().Be(9);
        baseline.Scenarios["PERF-W-02"].Counters["llm.calls"].Should().Be(4);
        baseline.Quality.RecallAtK.Should().Be(1);
        baseline.Quality.ExtractionFalsePositiveRate.Should().Be(0);
    }

    [Fact]
    public void Gate_IdenticalReport_Passes()
    {
        var report = Report();
        var baseline = PerfBaselineDocument.FromSummary(report);

        var result = PerfRegressionGate.Evaluate(
            baseline,
            report,
            new PerfCounterChangeOverride(false, null));

        result.Passed.Should().BeTrue();
        result.Violations.Should().BeEmpty();
    }

    [Fact]
    public void Gate_OneExtraQuery_FailsWithoutOverride()
    {
        var baseline = PerfBaselineDocument.FromSummary(Report());

        var result = PerfRegressionGate.Evaluate(
            baseline,
            Report(recallQueries: 10),
            new PerfCounterChangeOverride(false, null));

        result.Passed.Should().BeFalse();
        result.Violations.Should().ContainSingle(message =>
            message.Contains("PERF-R-04", StringComparison.Ordinal) &&
            message.Contains("neo4j.queries", StringComparison.Ordinal) &&
            message.Contains("9 -> 10", StringComparison.Ordinal));
    }

    [Fact]
    public void Gate_OneExtraQuery_LabelWithoutJustification_StillFails()
    {
        var baseline = PerfBaselineDocument.FromSummary(Report());

        var result = PerfRegressionGate.Evaluate(
            baseline,
            Report(recallQueries: 10),
            new PerfCounterChangeOverride(true, "ordinary PR description"));

        result.Passed.Should().BeFalse();
        result.Violations.Should().Contain(message =>
            message.Contains("justification", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Gate_OneExtraQuery_LabelAndJustification_Passes()
    {
        var baseline = PerfBaselineDocument.FromSummary(Report());

        var result = PerfRegressionGate.Evaluate(
            baseline,
            Report(recallQueries: 10),
            new PerfCounterChangeOverride(
                true,
                "Summary\nPerf counter change justification: deliberate query for freshness"));

        result.Passed.Should().BeTrue();
        result.AcknowledgedCounterChanges.Should().ContainSingle();
    }

    [Fact]
    public void Gate_QualityDrop_CannotBeOverridden()
    {
        var baseline = PerfBaselineDocument.FromSummary(Report());

        var result = PerfRegressionGate.Evaluate(
            baseline,
            Report(recallAtK: 0.99),
            new PerfCounterChangeOverride(
                true,
                "Perf counter change justification: deliberate query for freshness"));

        result.Passed.Should().BeFalse();
        result.Violations.Should().Contain(message =>
            message.Contains("recallAtK", StringComparison.Ordinal));
    }

    [Fact]
    public void Gate_BytesIncreaseAboveFivePercent_Fails()
    {
        var baseline = PerfBaselineDocument.FromSummary(Report(bytesEstimate: 100));

        var result = PerfRegressionGate.Evaluate(
            baseline,
            Report(bytesEstimate: 106),
            new PerfCounterChangeOverride(false, null));

        result.Passed.Should().BeFalse();
        result.Violations.Should().Contain(message =>
            message.Contains("bytes_est", StringComparison.Ordinal) &&
            message.Contains("5%", StringComparison.Ordinal));
    }

    [Fact]
    public void BaselineFromSummary_NondeterministicCounter_IsRejected()
    {
        var act = () => PerfBaselineDocument.FromSummary(Report(recallQueriesMin: 8));

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*neo4j.queries*deterministic*");
    }

    [Fact]
    public void BaselineFromSummary_NonScaleSReport_IsRejected()
    {
        var act = () => PerfBaselineDocument.FromSummary(Report(scale: "M"));

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*profile hermetic, scale S*");
    }

    [Fact]
    public void Baseline_SaveAndLoad_RoundTrips()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"agentmemory-perf-baseline-{Guid.NewGuid():N}.json");
        try
        {
            PerfBaselineDocument.FromSummary(Report()).Save(path);

            var loaded = PerfBaselineDocument.Load(path);

            loaded.Profile.Should().Be("hermetic-S");
            loaded.Scenarios["PERF-R-04"].Counters["neo4j.queries"].Should().Be(9);
            loaded.Quality.RecallAtK.Should().Be(1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task GateCommand_ExtraQuery_ReturnsNonZero()
    {
        var baselinePath = Path.Combine(
            Path.GetTempPath(),
            $"agentmemory-perf-baseline-{Guid.NewGuid():N}.json");
        var reportPath = Path.Combine(
            Path.GetTempPath(),
            $"agentmemory-perf-report-{Guid.NewGuid():N}.json");
        try
        {
            PerfBaselineDocument.FromSummary(Report()).Save(baselinePath);
            await File.WriteAllTextAsync(reportPath, Report(recallQueries: 10).GetRawText());
            var output = new StringWriter();

            var exitCode = await new PerfGateCommand(output).ExecuteAsync(
                baselinePath,
                reportPath,
                allowCounterChangeValue: null,
                pullRequestBody: null);

            exitCode.Should().Be(1);
            output.ToString().Should().Contain("perf gate: FAIL");
            output.ToString().Should().Contain("neo4j.queries increased: 9 -> 10");
        }
        finally
        {
            File.Delete(baselinePath);
            File.Delete(reportPath);
        }
    }

    [Fact]
    public async Task BaselineCommand_WithoutExplicitUpdate_ReturnsNonZero()
    {
        var output = new StringWriter();

        var exitCode = await new PerfBaselineCommand(output).ExecuteAsync(null, null, null);

        exitCode.Should().Be(1);
        output.ToString().Should().Contain("--update");
    }

    private static JsonElement Report(
        long recallQueries = 9,
        long? recallQueriesMin = null,
        double recallAtK = 1,
        long? bytesEstimate = null,
        string scale = "S")
    {
        static object Counter(long min, long max) => new
        {
            median = (double)max,
            min,
            max,
            deterministic = min == max,
        };

        var recallCounters = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["neo4j.tx.read"] = Counter(6, 6),
            ["neo4j.tx.write"] = Counter(1, 1),
            ["neo4j.queries"] = Counter(recallQueriesMin ?? recallQueries, recallQueries),
            ["embed.requests"] = Counter(1, 1),
            ["items.retrieved"] = Counter(43, 43),
        };
        if (bytesEstimate is not null)
            recallCounters["neo4j.bytes_est"] = Counter(bytesEstimate.Value, bytesEstimate.Value);

        var writeCounters = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["neo4j.tx.read"] = Counter(4, 4),
            ["neo4j.tx.write"] = Counter(18, 18),
            ["neo4j.queries"] = Counter(43, 43),
            ["embed.requests"] = Counter(4, 4),
            ["llm.calls"] = Counter(4, 4),
        };

        return JsonSerializer.SerializeToElement(new
        {
            manifest = new { profile = "hermetic", scale },
            qualityGate = new { tolerance = 0d },
            quality = new
            {
                recallAtK,
                mrr = 1d,
                casesWithViolations = 0,
            },
            extractionQuality = new
            {
                entityPrecision = 1d,
                entityRecall = 1d,
                factPrecision = 1d,
                factRecall = 1d,
                preferencePrecision = 1d,
                preferenceRecall = 1d,
                falsePositiveRate = 0d,
            },
            scenarios = new object[]
            {
                new
                {
                    scenario = "PERF-R-04",
                    counters = recallCounters,
                },
                new
                {
                    scenario = "PERF-W-02",
                    counters = writeCounters,
                },
            },
        });
    }
}
