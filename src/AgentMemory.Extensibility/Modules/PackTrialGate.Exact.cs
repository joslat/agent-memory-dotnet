using System.Globalization;
using System.Numerics;

namespace AgentMemory.Extensibility.Modules;

/// <summary>
/// The exact decision (feature 02): whether a bound reaches its target, decided in rational arithmetic, so every implementation
/// reaches the same verdict (eng/trial-statistics/exact.py is the reference; golden.json carries a grid of its decisions). A
/// verdict never needs a bound's value, only whether it reaches the target t:
/// <list type="bullet">
/// <item>Clopper-Pearson reaches t exactly when P(X ≥ x) at rate t is at most 1 − confidence, a finite sum of rationals.</item>
/// <item>A mean less q times its standard error reaches t exactly when mean − t ≥ 0 and (mean − t)² ≥ q² se², every term a
/// ratio of counts, quarters and the quantile table's decimals.</item>
/// <item>Fieller's lower root of a θ² + b θ + c (a &gt; 0) reaches t exactly when a t² + b t + c ≥ 0 and 2 a t ≤ −b.</item>
/// </list>
/// The bounds' clamps are kept. The floating-point bounds remain, for what a person reads; a verdict is never taken from them.
/// </summary>
public static partial class PackTrialGate
{
    /// <summary>A rational, reduced, with a positive denominator.</summary>
    internal readonly struct Q : IComparable<Q>, IEquatable<Q>
    {
        private Q(BigInteger n, BigInteger d) => (N, D) = (n, d);

        internal BigInteger N { get; }
        internal BigInteger D { get; }

        internal static Q Of(BigInteger n, BigInteger d)
        {
            if (d.IsZero) throw new DivideByZeroException();
            if (d.Sign < 0) (n, d) = (-n, -d);
            var g = BigInteger.GreatestCommonDivisor(n, d);
            return g.IsOne || g.IsZero ? new(n, d) : new(n / g, d / g);
        }

        /// <summary>A decimal as written (<c>1.6448536</c>), exactly.</summary>
        internal static Q Decimal(string text)
        {
            var dot = text.IndexOf('.');
            if (dot < 0) return Of(BigInteger.Parse(text, CultureInfo.InvariantCulture), 1);
            var digits = text.Length - dot - 1;
            return Of(BigInteger.Parse(text.Remove(dot, 1), CultureInfo.InvariantCulture), BigInteger.Pow(10, digits));
        }

        /// <summary>A double as the shortest decimal that reads back as it: the literal a table was written with.</summary>
        internal static Q Literal(double value) => Decimal(value.ToString("R", CultureInfo.InvariantCulture));

        public static implicit operator Q(long value) => new(value, 1);
        public static Q operator +(Q a, Q b) => Of(a.N * b.D + b.N * a.D, a.D * b.D);
        public static Q operator -(Q a, Q b) => Of(a.N * b.D - b.N * a.D, a.D * b.D);
        public static Q operator -(Q a) => new(-a.N, a.D);
        public static Q operator *(Q a, Q b) => Of(a.N * b.N, a.D * b.D);
        public static Q operator /(Q a, Q b) => Of(a.N * b.D, a.D * b.N);
        public static bool operator <(Q a, Q b) => a.CompareTo(b) < 0;
        public static bool operator >(Q a, Q b) => a.CompareTo(b) > 0;
        public static bool operator <=(Q a, Q b) => a.CompareTo(b) <= 0;
        public static bool operator >=(Q a, Q b) => a.CompareTo(b) >= 0;
        public static bool operator ==(Q a, Q b) => a.Equals(b);
        public static bool operator !=(Q a, Q b) => !a.Equals(b);
        internal int Sign => N.Sign;
        internal static Q Max(Q a, Q b) => a >= b ? a : b;
        public int CompareTo(Q other) => (N * other.D).CompareTo(other.N * D);
        public bool Equals(Q other) => N == other.N && D == other.D;
        public override bool Equals(object? obj) => obj is Q q && Equals(q);
        public override int GetHashCode() => HashCode.Combine(N, D);
        public override string ToString() => $"{N}/{D}";
    }

    // The hedge weights in quarters, and the confidences' complements, by confidence in thousandths.
    private static Q ExactHedge(int permille) => permille switch
    {
        900 => Q.Of(5, 4), 950 => Q.Of(3, 2), 975 => Q.Of(7, 4), 990 => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(permille), "A trial's confidence is 900, 950, 975 or 990 thousandths."),
    };

    private static Q Alpha(int permille) => Q.Of(1000 - permille, 1000);

    // Student's t quantile exactly: the table's decimal for 1 to 60 degrees of freedom, Cornish-Fisher in the normal quantile's
    // decimal beyond.
    private static Q ExactQuantile(int permille, int degrees)
    {
        var z = Confidences[permille / 1000.0];
        var v = Math.Max(1, degrees);
        if (v <= 60)
            foreach (var (key, row) in StudentQuantiles)
                if (Math.Abs(key - z) < 1e-6) return Q.Literal(row[v - 1]);
        var x = Q.Literal(z);
        var (x3, x5, x7) = (x * x * x, x * x * x * x * x, x * x * x * x * x * x * x);
        return x + (x3 + x) / (4L * v) + (5L * x5 + 16L * x3 + 3L * x) / (96L * v * v) + (3L * x7 + 19L * x5 + 17L * x3 - 15L * x) / (384L * v * v * v);
    }

    /// <summary>Whether Clopper-Pearson's bound of <paramref name="successes"/> in <paramref name="trials"/> reaches <paramref name="target"/>.</summary>
    internal static bool MeetsRate(int successes, int trials, int permille, Q target)
    {
        ExactHedge(permille);
        if (target.Sign <= 0) return true;
        if (trials <= 0 || successes <= 0 || target >= 1) return false;
        var (p, q) = (target, 1 - target);
        Q tail = 0;
        BigInteger choose = 1;
        for (var j = 0; j <= trials; j++)
        {
            if (j > 0) choose = choose * (trials - j + 1) / j;
            if (j >= successes) tail += Q.Of(choose, 1) * Power(p, j) * Power(q, trials - j);
        }
        return tail <= Alpha(permille);
    }

    private static Q Power(Q value, int exponent) => Q.Of(BigInteger.Pow(value.N, exponent), BigInteger.Pow(value.D, exponent));

    // se² of the values with the unseen one counted w times beside them; with lag1, the lag-1 autocovariance added, never lowering it.
    private static (Q Mean, Q Variance) ExactVariance(IReadOnlyList<Q> values, Q unseen, Q w, bool lag1)
    {
        var n = values.Count + w;
        Q sum = 0;
        foreach (var v in values) sum += v;
        var mean = (sum + w * unseen) / n;
        Q squares = w * (unseen - mean) * (unseen - mean);
        foreach (var v in values) squares += (v - mean) * (v - mean);
        var g0 = squares / (n - 1);
        if (!lag1) return (mean, g0 / n);
        Q g1 = 0;
        for (var i = 0; i + 1 < values.Count; i++) g1 += (values[i] - mean) * (values[i + 1] - mean);
        g1 /= n - 1;
        return (mean, Q.Max(g0, g0 + 2L * g1) / n);
    }

    // margin ≥ q √variance, with q > 0 and variance ≥ 0.
    private static bool AtLeast(Q margin, Q q, Q variance) => margin.Sign >= 0 && margin * margin >= q * q * variance;

    private static bool MeetsClustered(IReadOnlyList<PackTrialTask> units, int cap, int permille, Q target)
    {
        if (target.Sign <= 0) return true;
        long offered = units.Sum(u => (long)u.Offered);
        if (units.Count == 0 || offered == 0 || target > 1) return false;
        var w = ExactHedge(permille);
        var r = Q.Of(units.Sum(u => (long)u.Correct), 1) / (offered + w * cap);
        var ob = (offered + w * cap) / (units.Count + w);
        var (_, variance) = ExactVariance([.. units.Select(u => u.Correct - r * u.Offered)], -r * cap, w, lag1: true);
        return AtLeast((r - target) * ob, ExactQuantile(permille, units.Count - 1), variance);
    }

    /// <summary>Whether the pooled precision's bound of <paramref name="tasks"/> (Fieller with the unseen task; Clopper-Pearson at a cap of 1) reaches <paramref name="target"/>.</summary>
    internal static bool MeetsPrecision(IReadOnlyList<PackTrialTask> tasks, int maximumOffered, int permille, Q target)
    {
        var cap = Cap(maximumOffered);
        Within(tasks, cap);
        if (cap <= 1) return MeetsRate(tasks.Count(t => t.Correct > 0), tasks.Count(t => t.Offered > 0), permille, target);
        if (target.Sign <= 0) return true;
        if (tasks.Count == 0 || tasks.All(t => t.Offered == 0) || target > 1) return false;
        var w = ExactHedge(permille);
        var n = tasks.Count + w;
        var ob = (tasks.Sum(t => (long)t.Offered) + w * cap) / n;
        var cb = Q.Of(tasks.Sum(t => (long)t.Correct), 1) / n;
        var r = cb / ob;
        Q soo = w * (cap - ob) * (cap - ob), scc = w * cb * cb, sco = w * (cap - ob) * (0 - cb);
        foreach (var t in tasks)
        {
            soo += (t.Offered - ob) * (t.Offered - ob);
            scc += (t.Correct - cb) * (t.Correct - cb);
            sco += (t.Offered - ob) * (t.Correct - cb);
        }
        (soo, scc, sco) = (soo / (n - 1), scc / (n - 1), sco / (n - 1));
        var q = ExactQuantile(permille, tasks.Count - 1);
        var a = n * ob * ob - q * q * soo;
        var b = -2L * n * cb * ob + 2L * q * q * sco;
        var c = n * cb * cb - q * q * scc;
        if (a.Sign <= 0 || r < target) return false;
        if (b * b - 4L * a * c < 0) return true; // the bound is the estimate r, and r reaches the target
        return a * target * target + b * target + c >= 0 && 2L * a * target <= -b;
    }

    private static bool MeetsPairedDifference(IReadOnlyList<PackTrialTask> first, IReadOnlyList<PackTrialTask> second, int cap1, int cap2, int permille, Q target,
        bool lag1 = false)
    {
        if (target <= -1) return true;
        if (target > 1) return false;
        var w = ExactHedge(permille);
        Q o1 = w * cap1, c1 = 0, o2 = w * cap2, c2 = w * cap2;
        for (var i = 0; i < first.Count; i++) { o1 += first[i].Offered; c1 += first[i].Correct; o2 += second[i].Offered; c2 += second[i].Correct; }
        var n = first.Count + w;
        var (r1, r2, m1, m2) = (c1 / o1, c2 / o2, o1 / n, o2 / n);
        var f = new Q[first.Count];
        for (var i = 0; i < first.Count; i++) f[i] = (first[i].Correct - r1 * first[i].Offered) / m1 - (second[i].Correct - r2 * second[i].Offered) / m2;
        var (_, variance) = ExactVariance(f, (0 - r1 * cap1) / m1 - (cap2 - r2 * cap2) / m2, w, lag1);
        return AtLeast(r1 - r2 - target, ExactQuantile(permille, first.Count - 1), variance);
    }

    /// <summary>Whether an arm's hit-rate bound, as its trial is checked, reaches <paramref name="target"/>.</summary>
    internal static bool MeetsHitRate(PackTrialMeasurement arm, int permille, Q target)
    {
        ArgumentNullException.ThrowIfNull(arm);
        var m = ClusterSize(arm);
        return m == 1 ? MeetsRate(arm.Hits, arm.Tasks, permille, target) : MeetsClustered(HitClusters(TasksOf(arm), m), m, permille, target);
    }

    /// <summary>Whether an arm's pooled-precision bound, as its trial is checked, reaches <paramref name="target"/>.</summary>
    internal static bool MeetsPrecision(PackTrialMeasurement arm, int permille, Q target)
    {
        ArgumentNullException.ThrowIfNull(arm);
        var (m, cap, tasks) = (ClusterSize(arm), CapOf(arm), TasksOf(arm));
        if (m == 1) return MeetsPrecision(tasks, cap, permille, target);
        Within(tasks, cap);
        return MeetsClustered(Clusters(tasks, m), m * cap, permille, target);
    }

    /// <summary>Whether the paired hit-rate difference's bound reaches <paramref name="target"/>.</summary>
    internal static bool MeetsHitDifference(PackTrialMeasurement first, PackTrialMeasurement second, int permille, Q target)
    {
        var (a, b) = Paired(first, second);
        if (Math.Max(ClusterSize(first), ClusterSize(second)) is var m and > 1)
            return MeetsPairedDifference(HitClusters(a, m), HitClusters(b, m), m, m, permille, target, lag1: true);
        if (target <= -1) return true;
        if (target > 1) return false;
        var d = new Q[a.Count];
        for (var i = 0; i < a.Count; i++) d[i] = (a[i].Correct > 0 ? 1 : 0) - (b[i].Correct > 0 ? 1 : 0);
        var (mean, variance) = ExactVariance(d, -1, ExactHedge(permille), lag1: false);
        return AtLeast(mean - target, ExactQuantile(permille, a.Count - 1), variance);
    }

    /// <summary>Whether the paired precision difference's bound reaches <paramref name="target"/>.</summary>
    internal static bool MeetsPrecisionDifference(PackTrialMeasurement first, PackTrialMeasurement second, int permille, Q target)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        var (cap1, cap2) = (CapOf(first), CapOf(second));
        if (cap2 == 0) return cap1 == 0 ? target.Sign <= 0 : MeetsPrecision(first, permille, target);
        // −(the upper bound) reaches t when the wrong items' lower bound reaches 1 + t.
        if (cap1 == 0) return MeetsPrecision(second with { PerTask = [.. TasksOf(second).Select(t => t with { Correct = t.Offered - t.Correct })] }, permille, 1 + target);
        var (a, b) = Paired(first, second);
        Within(a, cap1);
        Within(b, cap2);
        return Math.Max(ClusterSize(first), ClusterSize(second)) is var m and > 1
            ? MeetsPairedDifference(Clusters(a, m), Clusters(b, m), m * cap1, m * cap2, permille, target, lag1: true)
            : MeetsPairedDifference(a, b, cap1, cap2, permille, target);
    }
}
