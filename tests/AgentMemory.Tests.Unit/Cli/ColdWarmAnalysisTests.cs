using AgentMemory.Cli.Perf;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Cli;

public sealed class ColdWarmAnalysisTests
{
    [Fact]
    public void Analyze_KeepsColdSamplesOrderedAndSeparateFromWarm()
    {
        var cold = new[]
        {
            Sample(30, bytes: 101, chars: 51),
            Sample(10, bytes: 102, chars: 52),
            Sample(20, bytes: 103, chars: 53),
        };
        var warm = new[]
        {
            Sample(8, bytes: 201, chars: 61),
            Sample(10, bytes: 202, chars: 62),
            Sample(12, bytes: 203, chars: 63),
        };

        var result = ColdWarmAnalyzer.Analyze(cold, warm);

        result.ColdMilliseconds.Should().Equal(30, 10, 20);
        result.WarmMilliseconds.Should().Equal(8, 10, 12);
        result.ColdMedianMs.Should().Be(20);
        result.WarmMedianMs.Should().Be(10);
        result.ColdPenaltyRatio.Should().Be(2);
    }

    [Fact]
    public void Analyze_RejectsStructuralCounterMismatch()
    {
        var cold = new[]
        {
            Sample(20),
            Sample(21, queries: 8),
        };

        var act = () => ColdWarmAnalyzer.Analyze(cold, [Sample(10)]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*neo4j.queries*");
    }

    [Fact]
    public void Analyze_RejectsQueryFingerprintMismatch()
    {
        var cold = new[]
        {
            Sample(20),
            Sample(21, fingerprint: "FactQueries.SearchByVector"),
        };

        var act = () => ColdWarmAnalyzer.Analyze(cold, [Sample(10)]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*query fingerprint*");
    }

    [Fact]
    public void Analyze_AllowsDataDependentPayloadAndCharacterDifferences()
    {
        var cold = new[]
        {
            Sample(20, bytes: 100, chars: 50),
            Sample(21, bytes: 200, chars: 60),
        };
        var warm = new[]
        {
            Sample(10, bytes: 300, chars: 70),
        };

        var act = () => ColdWarmAnalyzer.Analyze(cold, warm);

        act.Should().NotThrow();
    }

    [Fact]
    public void CacheManifest_DoesNotClaimPageOrFilesystemCachesWereReset()
    {
        var manifest = PerfCacheResetManifest.ColdSingleShot;

        manifest.Process.Should().StartWith("reset");
        manifest.Neo4jQueryPlanCache.Should().StartWith("cleared");
        manifest.Neo4jPageCache.Should().StartWith("not reset");
        manifest.OsFilesystemCache.Should().StartWith("not reset");
    }

    private static ColdWarmSample Sample(
        double milliseconds,
        long queries = 9,
        long bytes = 144_591,
        long chars = 3_906,
        string fingerprint = "MessageQueries.GetRecentBySession") =>
        new(
            "PERF-R-04",
            milliseconds,
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["neo4j.queries"] = queries,
                ["neo4j.tx.read"] = 6,
                ["neo4j.tx.write"] = 1,
                ["neo4j.records"] = 43,
                ["neo4j.bytes_est"] = bytes,
                ["context.chars"] = chars,
                ["items.retrieved"] = 43,
            },
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                [fingerprint] = 1,
            },
            "hermetic",
            "S",
            "zero");
}
