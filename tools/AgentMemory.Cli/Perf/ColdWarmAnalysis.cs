namespace AgentMemory.Cli.Perf;

/// <summary>One child-process sample loaded from its own immutable artifact.</summary>
public sealed record ColdWarmSample(
    string Scenario,
    double DurationMs,
    IReadOnlyDictionary<string, long> Counters,
    IReadOnlyDictionary<string, long> QueryFingerprints,
    string Profile,
    string Scale,
    string Latency);

/// <summary>
/// Cold and warm timing populations kept deliberately separate. Cold values remain in execution order;
/// only their median is derived, never a percentile distribution.
/// </summary>
public sealed record ColdWarmAnalysis(
    string Scenario,
    IReadOnlyList<double> ColdMilliseconds,
    IReadOnlyList<double> WarmMilliseconds,
    double ColdMedianMs,
    double WarmMedianMs,
    double ColdPenaltyRatio,
    IReadOnlyDictionary<string, long> StructuralCounters,
    IReadOnlyDictionary<string, long> QueryFingerprints);

/// <summary>Truthful cache-reset claims emitted into both child and parent run manifests.</summary>
public sealed record PerfCacheResetManifest(
    string Process,
    string Jit,
    string ServiceProvider,
    string DriverConnectionPool,
    string Neo4jQueryPlanCache,
    string Neo4jPageCache,
    string OsFilesystemCache)
{
    public static PerfCacheResetManifest ColdSingleShot { get; } = new(
        "reset: new OS child process per cold sample",
        "reset: new process; setup may JIT shared paths before the measured turn",
        "reset: new dependency-injection service provider",
        "reset: new Neo4j driver and connection pool",
        "cleared explicitly after scenario preparation and immediately before the measured turn",
        "not reset: database/schema/fixture setup may populate it",
        "not reset: host filesystem cache is outside the harness");
}

public static class ColdWarmAnalyzer
{
    private static readonly HashSet<string> DataDependentCounters = new(StringComparer.Ordinal)
    {
        "neo4j.bytes_est",
    };

    public static ColdWarmAnalysis Analyze(
        IReadOnlyList<ColdWarmSample> cold,
        IReadOnlyList<ColdWarmSample> warm)
    {
        ArgumentNullException.ThrowIfNull(cold);
        ArgumentNullException.ThrowIfNull(warm);
        if (cold.Count == 0)
            throw new ArgumentException("At least one cold sample is required.", nameof(cold));
        if (warm.Count == 0)
            throw new ArgumentException("At least one warm sample is required.", nameof(warm));

        var reference = cold[0];
        foreach (var sample in cold.Concat(warm))
        {
            ValidateFingerprint(reference, sample);
            ValidateStructuralCounters(reference, sample);
            ValidateQueryFingerprints(reference, sample);
            if (!double.IsFinite(sample.DurationMs) || sample.DurationMs <= 0)
            {
                throw new InvalidOperationException(
                    $"Invalid duration {sample.DurationMs} for {sample.Scenario}.");
            }
        }

        var coldMedian = Median(cold.Select(s => s.DurationMs));
        var warmMedian = Median(warm.Select(s => s.DurationMs));
        return new ColdWarmAnalysis(
            reference.Scenario,
            cold.Select(s => s.DurationMs).ToArray(),
            warm.Select(s => s.DurationMs).ToArray(),
            coldMedian,
            warmMedian,
            coldMedian / warmMedian,
            StructuralCounters(reference.Counters),
            new Dictionary<string, long>(reference.QueryFingerprints, StringComparer.Ordinal));
    }

    internal static bool IsStructuralCounter(string name) =>
        !DataDependentCounters.Contains(name) &&
        !name.EndsWith(".chars", StringComparison.Ordinal) &&
        !name.EndsWith(".tokens", StringComparison.Ordinal);

    private static void ValidateFingerprint(ColdWarmSample expected, ColdWarmSample actual)
    {
        if (!string.Equals(expected.Scenario, actual.Scenario, StringComparison.Ordinal) ||
            !string.Equals(expected.Profile, actual.Profile, StringComparison.Ordinal) ||
            !string.Equals(expected.Scale, actual.Scale, StringComparison.Ordinal) ||
            !string.Equals(expected.Latency, actual.Latency, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Cold/warm sample fingerprint mismatch: " +
                $"expected {expected.Scenario}/{expected.Profile}/{expected.Scale}/{expected.Latency}, " +
                $"got {actual.Scenario}/{actual.Profile}/{actual.Scale}/{actual.Latency}.");
        }
    }

    private static void ValidateStructuralCounters(ColdWarmSample expected, ColdWarmSample actual)
    {
        var expectedStructural = StructuralCounters(expected.Counters);
        var actualStructural = StructuralCounters(actual.Counters);
        foreach (var name in expectedStructural.Keys.Union(actualStructural.Keys, StringComparer.Ordinal))
        {
            expectedStructural.TryGetValue(name, out var expectedValue);
            actualStructural.TryGetValue(name, out var actualValue);
            if (expectedValue != actualValue)
            {
                throw new InvalidOperationException(
                    $"Structural counter '{name}' differs between cold/warm samples: " +
                    $"{expectedValue} != {actualValue}.");
            }
        }
    }

    private static void ValidateQueryFingerprints(ColdWarmSample expected, ColdWarmSample actual)
    {
        foreach (var name in expected.QueryFingerprints.Keys.Union(
                     actual.QueryFingerprints.Keys,
                     StringComparer.Ordinal))
        {
            expected.QueryFingerprints.TryGetValue(name, out var expectedValue);
            actual.QueryFingerprints.TryGetValue(name, out var actualValue);
            if (expectedValue != actualValue)
            {
                throw new InvalidOperationException(
                    $"Safe query fingerprint '{name}' differs between cold/warm samples: " +
                    $"{expectedValue} != {actualValue}.");
            }
        }
    }

    private static IReadOnlyDictionary<string, long> StructuralCounters(
        IReadOnlyDictionary<string, long> counters) =>
        counters
            .Where(kv => IsStructuralCounter(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(v => v).ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 1
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2;
    }
}
