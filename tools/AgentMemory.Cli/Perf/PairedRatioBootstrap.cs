namespace AgentMemory.Cli.Perf;

internal enum TimingVerdict
{
    Improvement,
    NoSignificantDifference,
    Regression,
}

internal sealed record PairedRatioResult(
    double Estimate,
    double Lower95,
    double Upper95,
    TimingVerdict Verdict,
    int Pairs);

/// <summary>
/// Deterministic percentile bootstrap over paired candidate/control ratios. The point estimate is the
/// geometric mean, which treats a 2x slowdown and a 2x speedup symmetrically in log space.
/// </summary>
internal static class PairedRatioBootstrap
{
    /// <summary>
    /// Clusters three consecutive AB/BA cycles as one bootstrap unit. Each six-pair block balances
    /// execution position while preserving the longer-lived Docker/driver timing correlation that made
    /// a two-pair bootstrap produce confidently wrong null results.
    /// </summary>
    public static PairedRatioResult AnalyzeCounterbalanced(
        IReadOnlyList<double> control,
        IReadOnlyList<double> candidate,
        int resamples = 10_000,
        int seed = 21)
    {
        if (control.Count != candidate.Count || control.Count == 0)
            throw new ArgumentException("control and candidate must contain the same non-zero number of pairs.");
        if (control.Count < 12 || control.Count % 6 != 0)
        {
            throw new ArgumentException(
                "counterbalanced timings require a multiple of six and at least twelve AB/BA pairs.");
        }
        if (control.Any(v => !double.IsFinite(v) || v <= 0) ||
            candidate.Any(v => !double.IsFinite(v) || v <= 0))
        {
            throw new ArgumentException("paired timings must be finite and positive.");
        }

        const int pairsPerBlock = 6;
        var blockControl = Enumerable.Repeat(1d, control.Count / pairsPerBlock).ToArray();
        var blockCandidate = new double[blockControl.Length];
        for (var block = 0; block < blockCandidate.Length; block++)
        {
            var start = block * pairsPerBlock;
            var meanLogRatio = Enumerable.Range(start, pairsPerBlock)
                .Average(i => Math.Log(candidate[i] / control[i]));
            blockCandidate[block] = Math.Exp(meanLogRatio);
        }

        return Analyze(blockControl, blockCandidate, resamples, seed);
    }

    public static PairedRatioResult Analyze(
        IReadOnlyList<double> control,
        IReadOnlyList<double> candidate,
        int resamples = 10_000,
        int seed = 21)
    {
        if (control.Count != candidate.Count || control.Count == 0)
            throw new ArgumentException("control and candidate must contain the same non-zero number of pairs.");
        if (resamples <= 0)
            throw new ArgumentOutOfRangeException(nameof(resamples), "resamples must be positive.");
        if (control.Any(v => !double.IsFinite(v) || v <= 0) ||
            candidate.Any(v => !double.IsFinite(v) || v <= 0))
        {
            throw new ArgumentException("paired timings must be finite and positive.");
        }

        var logRatios = control
            .Zip(candidate, (a, b) => Math.Log(b / a))
            .ToArray();
        var estimate = Math.Exp(logRatios.Average());

        var random = new Random(seed);
        var bootstrap = new double[resamples];
        for (var sample = 0; sample < resamples; sample++)
        {
            var sum = 0d;
            for (var pair = 0; pair < logRatios.Length; pair++)
                sum += logRatios[random.Next(logRatios.Length)];
            bootstrap[sample] = Math.Exp(sum / logRatios.Length);
        }

        Array.Sort(bootstrap);
        var lower = Quantile(bootstrap, 0.025);
        var upper = Quantile(bootstrap, 0.975);
        var verdict = upper < 1
            ? TimingVerdict.Improvement
            : lower > 1
                ? TimingVerdict.Regression
                : TimingVerdict.NoSignificantDifference;

        return new PairedRatioResult(estimate, lower, upper, verdict, control.Count);
    }

    private static double Quantile(IReadOnlyList<double> sorted, double probability)
    {
        if (sorted.Count == 1) return sorted[0];
        var position = (sorted.Count - 1) * probability;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return sorted[lower];
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * (position - lower));
    }
}
