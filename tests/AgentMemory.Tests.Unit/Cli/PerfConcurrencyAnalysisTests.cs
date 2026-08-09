using AgentMemory.Cli.Perf;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Cli;

public sealed class PerfConcurrencyAnalysisTests
{
    [Fact]
    public void Percentiles_IncludeP99_UsingTheHarnessInterpolationConvention()
    {
        var distribution = ConcurrencyAnalysis.Percentiles([1, 2, 3, 4, 100]);

        distribution.P50.Should().Be(3);
        distribution.P95.Should().BeApproximately(80.8, 0.000001);
        distribution.P99.Should().BeApproximately(96.16, 0.000001);
        distribution.Min.Should().Be(1);
        distribution.Max.Should().Be(100);
    }

    [Fact]
    public void Validate_AcceptsExactConcurrentCorrectnessShape()
    {
        var snapshot = ValidSnapshot();

        ConcurrencyRunValidator.Validate(snapshot).Should().BeEmpty();
    }

    [Fact]
    public void Validate_RejectsEveryReliabilityAndTelemetryViolation()
    {
        var snapshot = ValidSnapshot() with
        {
            OperationErrors = 2,
            OwnerLeaks = 1,
            OwnerMisses = 1,
            DedupLiveFacts = 3,
            SupersessionLosersPresent = 9,
            SupersessionLosersClosed = 8,
            SupersessionEdges = 11,
            SupersessionWinnersLive = 7,
            CrossOwnerEdges = 1,
            TransactionEntryEstimateSamples = 0,
        };

        ConcurrencyRunValidator.Validate(snapshot).Should().BeEquivalentTo(
        [
            "operation-errors",
            "owner-leak",
            "owner-miss",
            "dedup-live-count",
            "supersession-loser-presence",
            "supersession-loser-closure",
            "supersession-edge-count",
            "supersession-winner-live",
            "supersession-cross-owner-edge",
            "transaction-entry-estimate-missing",
        ], options => options.WithStrictOrdering());
    }

    [Fact]
    public void Analyze_ReportsExactErrorRateAndAchievedThroughput()
    {
        var result = ConcurrencyAnalysis.Analyze(
            concurrency: 10,
            elapsedMilliseconds: 250,
            requestMilliseconds: [10, 20, 30, 40],
            transactionEntryEstimateMilliseconds: [1, 2, 3],
            operationErrors: 1);

        result.Requests.Should().Be(4);
        result.ErrorRate.Should().Be(0.25);
        result.AchievedOperationsPerSecond.Should().Be(16);
        result.RequestMilliseconds.P99.Should().BeApproximately(39.7, 0.000001);
        result.TransactionEntryDelayEstimateMilliseconds.P99.Should().BeApproximately(2.98, 0.000001);
    }

    private static ConcurrencyCorrectnessSnapshot ValidSnapshot() =>
        new(
            Concurrency: 10,
            OperationErrors: 0,
            OwnerLeaks: 0,
            OwnerMisses: 0,
            DedupLiveFacts: 1,
            SupersessionLosersPresent: 10,
            SupersessionLosersClosed: 10,
            SupersessionEdges: 10,
            SupersessionWinnersLive: 10,
            CrossOwnerEdges: 0,
            TransactionEntryEstimateSamples: 30);
}
