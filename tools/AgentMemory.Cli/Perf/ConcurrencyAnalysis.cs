namespace AgentMemory.Cli.Perf;

/// <summary>A percentile distribution serialized into M-18 artifacts.</summary>
internal sealed record ConcurrencyDistribution(
    double P50,
    double P95,
    double P99,
    double Min,
    double Max);

/// <summary>Timing and load aggregates for one workload at one concurrency level.</summary>
internal sealed record ConcurrencyLevelAnalysis(
    int Concurrency,
    int Requests,
    double ElapsedMilliseconds,
    double ErrorRate,
    double AchievedOperationsPerSecond,
    ConcurrencyDistribution RequestMilliseconds,
    ConcurrencyDistribution TransactionEntryDelayEstimateMilliseconds);

/// <summary>
/// Safe, content-free correctness totals for one M-18 concurrency level.
/// </summary>
internal sealed record ConcurrencyCorrectnessSnapshot(
    int Concurrency,
    int OperationErrors,
    int OwnerLeaks,
    int OwnerMisses,
    long DedupLiveFacts,
    long SupersessionLosersPresent,
    long SupersessionLosersClosed,
    long SupersessionEdges,
    long SupersessionWinnersLive,
    long CrossOwnerEdges,
    int TransactionEntryEstimateSamples);

internal static class ConcurrencyAnalysis
{
    internal static ConcurrencyLevelAnalysis Analyze(
        int concurrency,
        double elapsedMilliseconds,
        IReadOnlyList<double> requestMilliseconds,
        IReadOnlyList<double> transactionEntryEstimateMilliseconds,
        int operationErrors)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(concurrency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(elapsedMilliseconds);
        ArgumentNullException.ThrowIfNull(requestMilliseconds);
        ArgumentNullException.ThrowIfNull(transactionEntryEstimateMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(operationErrors);

        var requests = requestMilliseconds.Count;
        return new ConcurrencyLevelAnalysis(
            concurrency,
            requests,
            elapsedMilliseconds,
            requests == 0 ? 0 : (double)operationErrors / requests,
            requests * 1000.0 / elapsedMilliseconds,
            Percentiles(requestMilliseconds),
            Percentiles(transactionEntryEstimateMilliseconds));
    }

    internal static ConcurrencyDistribution Percentiles(IEnumerable<double> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var values = source.Order().ToList();
        if (values.Count == 0)
            return new ConcurrencyDistribution(0, 0, 0, 0, 0);

        return new ConcurrencyDistribution(
            Quantile(values, 0.50),
            Quantile(values, 0.95),
            Quantile(values, 0.99),
            values[0],
            values[^1]);
    }

    private static double Quantile(IReadOnlyList<double> sorted, double quantile)
    {
        if (sorted.Count == 1) return sorted[0];
        var position = (sorted.Count - 1) * quantile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return sorted[lower];
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }
}

internal static class ConcurrencyRunValidator
{
    internal static IReadOnlyList<string> Validate(ConcurrencyCorrectnessSnapshot snapshot)
    {
        var issues = new List<string>();
        if (snapshot.OperationErrors != 0)
            issues.Add("operation-errors");
        if (snapshot.OwnerLeaks != 0)
            issues.Add("owner-leak");
        if (snapshot.OwnerMisses != 0)
            issues.Add("owner-miss");
        if (snapshot.DedupLiveFacts != 1)
            issues.Add("dedup-live-count");
        if (snapshot.SupersessionLosersPresent != snapshot.Concurrency)
            issues.Add("supersession-loser-presence");
        if (snapshot.SupersessionLosersClosed != snapshot.Concurrency)
            issues.Add("supersession-loser-closure");
        if (snapshot.SupersessionEdges != snapshot.Concurrency)
            issues.Add("supersession-edge-count");
        if (snapshot.SupersessionWinnersLive != snapshot.Concurrency)
            issues.Add("supersession-winner-live");
        if (snapshot.CrossOwnerEdges != 0)
            issues.Add("supersession-cross-owner-edge");
        if (snapshot.TransactionEntryEstimateSamples == 0)
            issues.Add("transaction-entry-estimate-missing");
        return issues;
    }
}
