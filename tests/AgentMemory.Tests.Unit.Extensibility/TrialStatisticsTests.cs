#pragma warning disable AMEXT001
using System.Text.Json;
using AgentMemory.Extensibility.Modules;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// Root RB, review 5: the trial statistics harness, the guard every statistic change passes. The bounds agree with the
/// reference (eng/trial-statistics/reference.py, golden vectors); a target exactly at the truth passes at most about the
/// nominal rate in every regime reviews 4 and 5 named and in seeded random ones, rare tasks of many items and upgrades that
/// agree on most tasks among them; power stays above floors; and the properties the format states hold. Seeded, so exact.
/// </summary>
public sealed class TrialStatisticsTests
{
    private sealed record Golden(double Confidence, int Cap1, int Cap2, int Spacing, int[][] First, int[][] Second, double RateLower,
        double PrecisionLower, double PrecisionUpper, double HitRateLowerOf, double PrecisionLowerOf, double PrecisionDifferenceLower, double HitDifferenceLower);

    // Drawn a burst apart unless told otherwise: independent tasks (review 6, H4).
    private static PackTrialMeasurement Arm(IReadOnlyList<PackTrialTask> tasks, int? cap = null, int spacing = PackTrialGate.BurstLength) => new()
    {
        Tasks = tasks.Count, Hits = tasks.Count(t => t.Correct > 0), Offered = tasks.Sum(t => t.Offered), Correct = tasks.Sum(t => t.Correct),
        P95Ms = 1, PerTask = tasks, MaximumOffered = cap, Spacing = spacing,
    };

    [Fact]
    public void The_bounds_agree_with_the_reference()
    {
        var cases = JsonSerializer.Deserialize<Golden[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TrialStatistics", "golden.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        cases.Should().HaveCountGreaterThan(20);
        foreach (var g in cases)
        {
            var (a, b) = (g.First.Select(t => new PackTrialTask(t[0], t[1])).ToList(), g.Second.Select(t => new PackTrialTask(t[0], t[1])).ToList());
            var because = $"{a.Count} tasks at {g.Confidence}, cap {g.Cap1}";
            PackTrialGate.RateLower(a.Count(t => t.Correct > 0), a.Count, g.Confidence).Should().BeApproximately(g.RateLower, 1e-9, because);
            PackTrialGate.PrecisionLower(a, g.Cap1, g.Confidence).Should().BeApproximately(g.PrecisionLower, 1e-9, because);
            PackTrialGate.PrecisionUpper(a, g.Cap1, g.Confidence).Should().BeApproximately(g.PrecisionUpper, 1e-9, because);
            because += $", spacing {g.Spacing}";
            PackTrialGate.HitRateLower(Arm(a, g.Cap1, g.Spacing), g.Confidence).Should().BeApproximately(g.HitRateLowerOf, 1e-9, because);
            PackTrialGate.PrecisionLower(Arm(a, g.Cap1, g.Spacing), g.Confidence).Should().BeApproximately(g.PrecisionLowerOf, 1e-9, because);
            PackTrialGate.PrecisionDifferenceLower(Arm(a, g.Cap1, g.Spacing), Arm(b, g.Cap2, g.Spacing), g.Confidence).Should().BeApproximately(g.PrecisionDifferenceLower, 1e-9, because);
            PackTrialGate.HitDifferenceLower(Arm(a, g.Cap1, g.Spacing), Arm(b, g.Cap2, g.Spacing), g.Confidence).Should().BeApproximately(g.HitDifferenceLower, 1e-9, because);
        }
    }

    // One task's (offered, correct): sizes with weights, a precision per size, a share of tasks all right or all wrong together.
    private sealed record Regime(int[] Sizes, double[] Weights, double[] Precision, double Together)
    {
        public double Truth
        {
            get
            {
                double items = 0, right = 0;
                for (var i = 0; i < Sizes.Length; i++) { items += Weights[i] * Sizes[i]; right += Weights[i] * Sizes[i] * Precision[i]; }
                return items == 0 ? 0 : right / items;
            }
        }

        public PackTrialTask Draw(Random rng)
        {
            var (u, i) = (rng.NextDouble() * Weights.Sum(), 0);
            while (i < Sizes.Length - 1 && (u -= Weights[i]) > 0) i++;
            var (o, p) = (Sizes[i], Precision[i]);
            if (rng.NextDouble() < Together) return new(o, rng.NextDouble() < p ? o : 0);
            var c = 0;
            for (var k = 0; k < o; k++) if (rng.NextDouble() < p) c++;
            return new(o, c);
        }
    }

    private static Regime Random(Random rng, int cap)
    {
        var support = Enumerable.Range(0, 1 + rng.Next(Math.Min(cap + 1, 5))).Select(_ => rng.Next(cap + 1)).Distinct().Order().ToArray();
        if (support.All(s => s == 0)) support = [0, 1];
        var weights = support.Select(_ => 0.05 + rng.NextDouble()).ToArray();
        var (basis, slope) = (0.02 + 0.96 * rng.NextDouble(), rng.Next(2) == 0 ? 0 : -0.8 + 1.6 * rng.NextDouble());
        var mean = support.Average();
        var precision = support.Select(s => Math.Clamp(basis + slope * (s - mean) / cap, 0.01, 0.99)).ToArray();
        return new(support, weights, precision, rng.Next(3) switch { 0 => 0, 1 => 1, _ => rng.NextDouble() });
    }

    // A rare task (about c/n of them) of `cap` items, all wrong, among tasks of one or two items at a good precision.
    private static Regime RareLoss(Random rng, int n, int cap)
    {
        var eps = new[] { 0.5, 1, 2, 3 }[rng.Next(4)] / n;
        var p = 0.3 + 0.6 * rng.NextDouble();
        return new([1, 2, cap], [(1 - eps) / 2, (1 - eps) / 2, eps], [p, p, 0], rng.Next(2));
    }

    private static readonly Regime[] Named =
    [
        new([1, 2, 3], [1, 1, 1], [.45, .45, .45], 0),
        new([1, 2, 3], [1, 1, 1], [.9, .9, .9], 1),
        new([0, 1, 2, 3], [1, 1, 1, 1], [.5, .5, .5, .5], 1),
        new([1, 3], [1, 1], [.67, .21], 0),        // review 5, M5: precision falls with task size
        new([1, 3], [4, 1], [.67, .21], 0),
        new([1, 16], [9, 1], [.6, .1], 1),
        new([1, 16], [19, 1], [.8, .95], 1),       // review 4: a few tasks offer many items
        new([1, 2, 3], [1, 1, 1], [.03, .03, .03], 0),
    ];

    private static double PassAtTruth(Regime regime, int n, int cap, double confidence, Random rng, int sims, double gap = 0)
    {
        var target = regime.Truth - gap;
        var passes = 0;
        for (var s = 0; s < sims; s++)
        {
            var tasks = Enumerable.Range(0, n).Select(_ => regime.Draw(rng)).ToList();
            if (tasks.All(t => t.Offered == 0)) continue;
            if (PackTrialGate.PrecisionLower(tasks, cap, confidence) >= target - 1e-12) passes++;
        }
        return (double)passes / sims;
    }

    // Two arms on the same tasks: the second from `second`; the first copies it on a share `agree` of tasks, else draws from `first`.
    private static double DifferencePassAtTruth(Regime second, double agree, Regime first, int n, double confidence, Random rng, int sims, double gap = 0)
    {
        double E(Regime r, Func<int, double> f) => r.Sizes.Select((s, i) => r.Weights[i] / r.Weights.Sum() * f(i)).Sum();
        var (o2, c2) = (E(second, i => second.Sizes[i]), E(second, i => second.Sizes[i] * second.Precision[i]));
        var (o1n, c1n) = (E(first, i => first.Sizes[i]), E(first, i => first.Sizes[i] * first.Precision[i]));
        var truth = (agree * c2 + (1 - agree) * c1n) / (agree * o2 + (1 - agree) * o1n) - c2 / o2 - gap;
        var passes = 0;
        for (var s = 0; s < sims; s++)
        {
            var b = Enumerable.Range(0, n).Select(_ => second.Draw(rng)).ToList();
            var a = b.Select(t => rng.NextDouble() < agree ? t : first.Draw(rng)).ToList();
            if (a.All(t => t.Offered == 0) || b.All(t => t.Offered == 0)) continue;
            if (PackTrialGate.PrecisionDifferenceLower(Arm(a, 3), Arm(b, 3), confidence) >= truth - 1e-12) passes++;
        }
        return (double)passes / sims;
    }

    [Theory]
    [InlineData(0.95, 0.07)]
    [InlineData(0.99, 0.025)]
    public void A_precision_equal_to_the_target_passes_at_most_about_the_nominal_rate(double confidence, double ceiling)
    {
        var rng = new Random(29);
        var worst = new List<(double Rate, string What)>();
        foreach (var n in new[] { 10, 40, 100 })
            foreach (var cap in new[] { 3, 16 })
            {
                foreach (var regime in Named.Where(r => r.Sizes.Max() <= cap)) worst.Add((PassAtTruth(regime, n, cap, confidence, rng, 400), $"named {regime}"));
                for (var i = 0; i < 12; i++) { var r = Random(rng, cap); worst.Add((PassAtTruth(r, n, cap, confidence, rng, 400), $"random {r}")); }
                for (var i = 0; i < 6; i++) { var r = RareLoss(rng, n, cap); worst.Add((PassAtTruth(r, n, cap, confidence, rng, 400), $"rare loss {r}")); }
            }
        var max = worst.MaxBy(w => w.Rate);
        max.Rate.Should().BeLessThanOrEqualTo(ceiling, max.What);
    }

    [Theory]
    [InlineData(0.95, 0.07)]
    [InlineData(0.99, 0.025)]
    public void A_difference_equal_to_the_target_passes_at_most_about_the_nominal_rate(double confidence, double ceiling)
    {
        var rng = new Random(31);
        var worst = new List<(double Rate, string What)>();
        var allOrNone = new Regime([0, 1, 2, 3], [1, 1, 1, 1], [.5, .5, .5, .5], 1);
        var broken = new Regime([0, 1, 2, 3], [1, 1, 1, 1], [0, 0, 0, 0], 1);
        foreach (var n in new[] { 10, 40, 100 })
        {
            // Review 5, M4: an upgrade that agrees with its predecessor on most tasks and breaks the rest.
            foreach (var agree in new[] { 0.9, 0.95, 0.98 })
                worst.Add((DifferencePassAtTruth(allOrNone, agree, broken, n, confidence, rng, 400), $"n {n}, agree {agree}"));
            for (var i = 0; i < 14; i++)
            {
                var (b, a, agree) = (Random(rng, 3), Random(rng, 3), new[] { 0, 0.5, 0.8, 0.95, 0.98 }[rng.Next(5)]);
                worst.Add((DifferencePassAtTruth(b, agree, a, n, confidence, rng, 300), $"n {n}, agree {agree}, {a} over {b}"));
            }
        }
        var max = worst.MaxBy(w => w.Rate);
        max.Rate.Should().BeLessThanOrEqualTo(ceiling, max.What);
    }

    [Fact]
    public void Power_on_forty_tasks_stays_above_its_floors()
    {
        // The reference harness measured 0.40-0.42 and 0.75-0.76 for precision 0.10 and 0.15 above its target, and 0.78-0.80
        // for a lift of 0.26 against a target of 0.05 (eng/trial-statistics); round 4 had 0.29, 0.73 and 0.93, with false
        // passes of up to a third in the regimes above.
        var rng = new Random(37);
        var iid = Named[0];
        PassAtTruth(iid, 40, 3, 0.95, rng, 800, gap: 0.15).Should().BeGreaterThan(0.65);
        PassAtTruth(iid, 40, 3, 0.95, rng, 800, gap: 0.10).Should().BeGreaterThan(0.30);
        var grep = new Regime([3], [1], [.19], 0);
        var pack = new Regime([1, 2, 3], [1, 1, 1], [.45, .45, .45], 0);
        DifferencePassAtTruth(grep, 0, pack, 40, 0.95, rng, 800, gap: 0.21).Should().BeGreaterThan(0.65);
    }

    [Fact]
    public void Bounds_do_not_depend_on_task_order_and_one_more_wrong_item_barely_ever_helps_one_arm()
    {
        var rng = new Random(41);
        var (rises, largest) = (0, 0.0);
        for (var s = 0; s < 2000; s++)
        {
            var regime = Random(rng, 3);
            var tasks = Enumerable.Range(0, 10 + rng.Next(50)).Select(_ => regime.Draw(rng)).ToList();
            if (tasks.All(t => t.Offered == 0)) continue;
            var bound = PackTrialGate.PrecisionLower(tasks, 3, 0.95);
            PackTrialGate.PrecisionLower([.. tasks.OrderBy(_ => rng.Next())], 3, 0.95).Should().BeApproximately(bound, 1e-12);
            var i = rng.Next(tasks.Count);
            if (tasks[i].Offered == 3) continue;
            var worse = tasks.ToList();
            worse[i] = worse[i] with { Offered = worse[i].Offered + 1 };
            var after = PackTrialGate.PrecisionLower(worse, 3, 0.95);
            if (after > bound + 1e-12) { rises++; largest = Math.Max(largest, after - bound); }
        }
        // Pack format v1.2 section 7: an added wrong item can raise a one-arm bound only by removing its task's own spread, rarely and by very little.
        rises.Should().BeLessThan(10);
        largest.Should().BeLessThan(0.005);
    }

    [Theory]
    [InlineData(0.95)]
    [InlineData(0.99)]
    public void Review6_a_paired_hit_difference_at_its_target_passes_at_most_the_nominal_rate_exactly(double confidence)
    {
        // H2: one unseen task was a rule of about 2.7 where the nominal level needs 3 (6.8% at 95%, 3.3% at 99%: an upgrade that
        // breaks a few tasks and fixes none). Every outcome of the trinomial (tasks only the first arm hits, only the second,
        // the rest), summed exactly at each pair of rates: no Monte Carlo. eng/trial-statistics/hitdiff_exact.py covers n 10 to 200.
        var worst = (Rate: 0.0, What: "");
        foreach (var n in new[] { 10, 40, 100 })
        {
            var bound = new double[n + 1, n + 1];
            for (var up = 0; up <= n; up++)
                for (var down = 0; up + down <= n; down++)
                {
                    var a = Enumerable.Range(0, n).Select(i => new PackTrialTask(1, i < up ? 1 : 0)).ToList();
                    var b = Enumerable.Range(0, n).Select(i => new PackTrialTask(1, i >= up && i < up + down ? 1 : 0)).ToList();
                    bound[up, down] = PackTrialGate.HitDifferenceLower(Arm(a, 1), Arm(b, 1), confidence);
                }
            var logFactorial = new double[n + 1];
            for (var i = 1; i <= n; i++) logFactorial[i] = logFactorial[i - 1] + Math.Log(i);
            var rates = new[] { 1e-12, 0.002, 0.01, 0.03, 0.06, 0.1, 0.2, 0.35, 0.5 };
            foreach (var pUp in rates)
                foreach (var pDown in rates)
                {
                    var pass = 0.0;
                    for (var up = 0; up <= n; up++)
                        for (var down = 0; up + down <= n; down++)
                            if (bound[up, down] >= pUp - pDown - 1e-12)
                                pass += Math.Exp(logFactorial[n] - logFactorial[up] - logFactorial[down] - logFactorial[n - up - down]
                                    + up * Math.Log(pUp) + down * Math.Log(pDown) + (n - up - down) * Math.Log(1 - pUp - pDown));
                    if (pass > worst.Rate) worst = (pass, $"n {n}, only the first {pUp}, only the second {pDown}");
                }
        }
        worst.Rate.Should().BeLessThanOrEqualTo(1 - confidence, worst.What);
    }

    [Theory]
    [InlineData(0.95, 0.07)]
    [InlineData(0.99, 0.025)]
    public void Review6_tasks_drawn_from_bursts_of_related_commits_pass_at_most_about_the_nominal_rate(double confidence, double ceiling)
    {
        // H4: commits come in bursts that share how well a pack does on them (here bursts of about 8 eligible commits, an
        // intraclass correlation of 0.3). Drawn one after another a hit-rate target at the truth passed 16 to 21% of the time
        // at 95%; spaced and clustered, at most about the nominal rate, whatever the history's length (eng/trial-statistics/serial.py).
        var rng = new Random(47);
        var worst = (Rate: 0.0, What: "");
        foreach (var eligible in new[] { 40, 80, 200, 400 })
        {
            var spacing = Math.Max(1, eligible / 40);
            var passes = 0;
            const int sims = 1500;
            for (var s = 0; s < sims; s++)
            {
                var hits = new bool[eligible];
                for (var i = 0; i < eligible;)
                {
                    var length = 1;
                    while (rng.NextDouble() > 1.0 / 8) length++;
                    var p = 0.5 + (rng.Next(2) == 0 ? -1 : 1) * Math.Sqrt(0.3 * 0.25);
                    for (var j = 0; j < length && i < eligible; j++, i++) hits[i] = rng.NextDouble() < p;
                }
                var tasks = Enumerable.Range(0, 40).Select(i => new PackTrialTask(1, hits[i * spacing] ? 1 : 0)).ToList();
                if (PackTrialGate.HitRateLower(Arm(tasks, 1, spacing), confidence) >= 0.5 - 1e-12) passes++;
            }
            if ((double)passes / sims > worst.Rate) worst = ((double)passes / sims, $"{eligible} eligible tasks, spacing {spacing}");
        }
        worst.Rate.Should().BeLessThanOrEqualTo(ceiling, worst.What);
    }

    [Fact]
    public void Review6_at_one_item_a_task_precision_is_Clopper_Pearsons()
    {
        // H3: with a cap of 1 each task that offers is one right-or-wrong trial; Fieller fell to a Wald bound there, above the
        // exact one (6.3% at 95%, 2.9% at 99%, at the truth). Ten tasks offer nothing, ten one wrong item, forty one right item.
        var tasks = Enumerable.Range(0, 60).Select(i => new PackTrialTask(i % 6 == 0 ? 0 : 1, i % 6 is 0 or 1 ? 0 : 1)).ToList();
        foreach (var confidence in new[] { 0.9, 0.95, 0.975, 0.99 })
            PackTrialGate.PrecisionLower(tasks, 1, confidence).Should().Be(PackTrialGate.RateLower(40, 50, confidence));
    }

    [Fact]
    public void Review6_a_baseline_that_offers_nothing_by_design_has_no_uncertainty_and_a_silent_host_keeps_its_hedge()
    {
        // M5: grep offers its three items on a few tasks, here on none of forty: its precision is unknown, not 0, so a lift over
        // it keeps grep's unseen task. Only the baseline none, a cap of 0, offers nothing by design.
        var rng = new Random(43);
        var pack = Enumerable.Range(0, 40).Select(_ => Named[0].Draw(rng)).ToList();
        var nothing = Enumerable.Repeat(new PackTrialTask(0, 0), 40).ToList();
        var own = PackTrialGate.PrecisionLower(pack, 3, 0.95);
        PackTrialGate.PrecisionDifferenceLower(Arm(pack, 3), Arm(nothing, 0), 0.95).Should().Be(own);
        PackTrialGate.PrecisionDifferenceLower(Arm(pack, 3), Arm(nothing, 3), 0.95).Should().BeLessThan(own - 0.5);
    }

    [Fact]
    public void Review6_without_per_task_results_no_precision_is_bounded()
    {
        // M6: items taken as independent passed all-or-none tasks up to a quarter of the time; a record without per-task results
        // now bounds no precision at all.
        var totals = new PackTrialMeasurement { Tasks = 40, Hits = 20, Offered = 120, Correct = 60, P95Ms = 1 };
        var none = new PackTrialMeasurement { Tasks = 40, Hits = 0, Offered = 0, Correct = 0, P95Ms = 1, MaximumOffered = 0 };
        FluentActions.Invoking(() => PackTrialGate.PrecisionDifferenceLower(totals, none, 0.95)).Should().Throw<ArgumentException>().WithMessage("*PerTask*");
        var manifest = ModuleManifest.Parse(
            """{"format":"agentmemory.module/v1alpha1","id":"com.example.base","version":"1.0.0","sdk":"[0.1,0.2)","trial":{"measure":"test-impact","tasks":40,"confidence":0.95,"targets":{"precision":0.25}}}""");
        FluentActions.Invoking(() => PackTrialGate.Decide(manifest, totals, none, DateTimeOffset.UnixEpoch)).Should().Throw<ArgumentException>().WithMessage("*PerTask*");
    }

    [Fact]
    public void Lint_says_what_a_declared_trial_can_never_show()
    {
        static ModuleTrial Trial(int tasks, string targets, string upgrade = "") => ModuleManifest.Parse(
            """{"format":"agentmemory.module/v1alpha1","id":"com.example.base","version":"1.0.0","sdk":"[0.1,0.2)","trial":{"measure":"test-impact","tasks":"""
            + tasks + ""","confidence":0.95,"targets":""" + targets + upgrade + "}}").Trial!;

        // The code map's own trial: every target reachable on forty tasks.
        PackTrialGate.Unreachable(Trial(40, """{"hitRate":0.35,"precision":0.25,"lift":-0.05,"precisionLift":0.05}"""), 3).Should().BeEmpty();
        // Ten tasks cannot bound a precision at 0.9, whatever the data; more tasks would.
        PackTrialGate.Unreachable(Trial(10, """{"precision":0.9}"""), 3).Should().ContainSingle().Which.Should().Contain("precision 0.9 cannot be met on 10 tasks").And.Contain("tasks would");
        // Review 5, M4: an upgrade exactly as good as this version cannot clear 0.05 on forty tasks; the note says what would,
        // and that a pack offering fewer items than its cap needs more (review 6, M7).
        PackTrialGate.Unreachable(Trial(40, """{"hitRate":0.35}""", ""","upgrade":{"noRegression":true,"tolerance":0.05}"""), 3)
            .Should().ContainSingle().Which.Should().Contain("cannot clear the tolerance 0.05 on 40 tasks").And.Contain("a tolerance of 0.09")
            .And.Contain("every task offers all 3 items");
        // Without a confidence nothing is bounded, so nothing falls short.
        PackTrialGate.Unreachable(Trial(10, """{"precision":0.9}""") with { Confidence = null }, 3).Should().BeEmpty();
    }

    [Fact]
    public void Forty_tasks_cannot_certify_a_tolerance_of_five_points_and_a_tighter_cap_is_a_tighter_bound()
    {
        // Review 5, M4: two versions that agree on every task. The unseen task could be one the upgrade breaks: on forty tasks
        // that leaves -0.086 (review 6: weighted 1.5 at 95%), so a tolerance of 0.05 cannot be shown, whatever the data.
        var same = Enumerable.Range(0, 40).Select(i => new PackTrialTask(3, i % 3)).ToList();
        PackTrialGate.PrecisionDifferenceLower(Arm(same, 3), Arm(same, 3), 0.95).Should().BeLessThan(-0.05);
        PackTrialGate.HitDifferenceLower(Arm(same, 3), Arm(same, 3), 0.95).Should().BeLessThan(-0.05);
        var long200 = Enumerable.Range(0, 200).Select(i => new PackTrialTask(3, i % 3)).ToList();
        PackTrialGate.PrecisionDifferenceLower(Arm(long200, 3), Arm(long200, 3), 0.95).Should().BeGreaterThan(-0.05);
        // Every item right on forty tasks of three: high, and below 1; the cap the arm declared makes the unseen task smaller.
        var right = Enumerable.Range(0, 40).Select(_ => new PackTrialTask(3, 3)).ToList();
        PackTrialGate.PrecisionLower(right, 3, 0.95).Should().BeInRange(0.9, 0.99);
        PackTrialGate.PrecisionLower(right, 16, 0.95).Should().BeLessThan(PackTrialGate.PrecisionLower(right, 3, 0.95));
        // Clopper-Pearson: no hits bounds nothing; all forty hits, the rule of three.
        PackTrialGate.RateLower(0, 40, 0.95).Should().Be(0);
        PackTrialGate.RateLower(40, 40, 0.95).Should().BeApproximately(Math.Pow(0.05, 1.0 / 40), 1e-9);
    }
}
