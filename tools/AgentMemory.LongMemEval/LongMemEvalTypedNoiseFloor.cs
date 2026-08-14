namespace AgentMemory.LongMemEval;

/// <summary>Repeat-variance of one memory type's accuracy across runs of the same arm.</summary>
/// <param name="MemoryType">The type.</param>
/// <param name="Runs">How many runs contributed.</param>
/// <param name="Questions">Questions of this type per run (they must agree across runs).</param>
/// <param name="MinAccuracy">Lowest accuracy observed.</param>
/// <param name="MaxAccuracy">Highest accuracy observed.</param>
/// <param name="MeanAccuracy">Mean accuracy.</param>
/// <param name="StandardDeviation">Sample standard deviation, or null below two runs.</param>
internal sealed record LongMemEvalTypedNoiseFloor(
    string MemoryType,
    int Runs,
    int Questions,
    double MinAccuracy,
    double MaxAccuracy,
    double MeanAccuracy,
    double? StandardDeviation)
{
    /// <summary>Observed spread in accuracy points — the crudest honest band.</summary>
    public double RangePoints => (MaxAccuracy - MinAccuracy) * 100.0;

    /// <summary>What a single question is worth, in accuracy points.</summary>
    public double PointsPerQuestion => Questions == 0 ? 0 : 100.0 / Questions;

    /// <summary>
    /// Whether a difference of <paramref name="points"/> is larger than the observed spread.
    /// </summary>
    /// <remarks>
    /// Deliberately crude and deliberately conservative. It is not a significance test: with a handful
    /// of runs the honest question is only "is this bigger than what the same configuration already
    /// varied by?", and a difference that fails that has no business being reported as a result.
    /// A single run yields no spread, so nothing is separable — which is the correct answer, not a
    /// missing feature.
    /// </remarks>
    public bool Separates(double points) => Runs >= 2 && points > RangePoints;
}

/// <summary>
/// Measures how much a memory type's accuracy varies across repeats of the same configuration.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this must exist before any per-type number is published.</b> A per-type subset is small —
/// episodic is 6 of 50 questions, i.e. <b>16.7 accuracy points per question</b> — and the whole-run
/// band (~±9 points at n=50) does not transfer to it. Quoting a per-type figure without its own band
/// is how a table invites exactly the comparison it cannot support.
/// </para>
/// <para>
/// It also cannot be inferred from question count alone: extraction on this deployment is
/// non-deterministic, so two builds of one configuration differ in what they stored, and the spread
/// that matters is the measured one rather than a binomial estimate.
/// </para>
/// </remarks>
internal static class LongMemEvalTypedNoiseFloorCalculator
{
    /// <summary>
    /// Per-type spread across runs. <paramref name="runs"/> must be repeats of the SAME arm and
    /// configuration; mixing arms measures the difference between them, not the noise within one.
    /// </summary>
    internal static IReadOnlyList<LongMemEvalTypedNoiseFloor> Measure(
        IReadOnlyList<IReadOnlyList<LongMemEvalTypedAccuracy>> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (runs.Count == 0) return [];

        var byType = new Dictionary<string, List<LongMemEvalTypedAccuracy>>(StringComparer.Ordinal);
        foreach (var run in runs)
            foreach (var row in run)
            {
                if (!byType.TryGetValue(row.MemoryType, out var list))
                    byType[row.MemoryType] = list = [];
                list.Add(row);
            }

        var results = new List<LongMemEvalTypedNoiseFloor>(byType.Count);
        foreach (var (type, rows) in byType)
        {
            // A type absent from some runs cannot have its spread measured against the others: the
            // denominators differ, so the comparison is between different questions.
            //
            // The same objection applies to a type PRESENT in both runs at different sizes, and until
            // this instrument was first wired up (25.7) the code did not enforce what this comment
            // said. Two accepted 50-question runs sampled 23 and 25 semantic questions; pooling them
            // reported a +/-17.4 point "noise band" that was mostly the difference between two
            // different question sets, and then labelled it with the FIRST run's denominator.
            //
            // So: only rows sharing a denominator are comparable. The largest such cohort is used, and
            // a type whose cohort has fewer than two runs reports no spread rather than a fabricated
            // one -- consistent with how a single run is treated everywhere else here.
            var cohort = rows
                .Where(row => row.Accuracy is not null)
                .GroupBy(row => row.Questions)
                .OrderByDescending(group => group.Count())
                .ThenByDescending(group => group.Key)
                .FirstOrDefault();
            if (cohort is null) continue;

            var accuracies = cohort.Select(row => row.Accuracy!.Value).ToArray();
            if (accuracies.Length == 0) continue;

            var mean = accuracies.Average();
            double? sd = accuracies.Length < 2
                ? null
                : Math.Sqrt(accuracies.Sum(a => (a - mean) * (a - mean)) / (accuracies.Length - 1));

            results.Add(new LongMemEvalTypedNoiseFloor(
                type,
                accuracies.Length,
                cohort.Key,
                accuracies.Min(),
                accuracies.Max(),
                mean,
                sd));
        }

        return results.OrderByDescending(r => r.Questions).ThenBy(r => r.MemoryType, StringComparer.Ordinal).ToArray();
    }
}
