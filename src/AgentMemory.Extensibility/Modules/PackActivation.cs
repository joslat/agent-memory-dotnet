using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentMemory.Extensibility.Modules;

/// <summary>What one arm of a trial measured.</summary>
[Experimental("AMEXT001")]
public sealed record PackTrialMeasurement
{
    /// <summary>Tasks run.</summary>
    public required int Tasks { get; init; }
    /// <summary>Tasks whose truth was offered.</summary>
    public required int Hits { get; init; }
    /// <summary>Items offered, and how many of them were in the truth.</summary>
    public required int Offered { get; init; }
    /// <summary>Offered items that were in the truth.</summary>
    public required int Correct { get; init; }
    /// <summary>The p95 compile time, in milliseconds.</summary>
    public required double P95Ms { get; init; }
    /// <summary>
    /// Each task's offered and correct items, in task order, when the runner kept them (PACK-gate-stats-2): the items one task
    /// offers are correlated, so precision's confidence bound treats each task as one cluster. Their sums must be
    /// <see cref="Offered"/> and <see cref="Correct"/>, one per task. Null: the bound treats items as independent.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PackTrialTask>? PerTask { get; init; }
    /// <summary><see cref="Hits"/> over <see cref="Tasks"/>.</summary>
    public double HitRate => Tasks == 0 ? 0 : (double)Hits / Tasks;
    /// <summary><see cref="Correct"/> over <see cref="Offered"/>.</summary>
    public double Precision => Offered == 0 ? 0 : (double)Correct / Offered;
}

/// <summary>
/// The version a trial compared an upgrade against (PACK-upgrade-gate): the one that passed on this host before, measured on
/// the same tasks in the same run.
/// </summary>
[Experimental("AMEXT001")]
public sealed record PackTrialPrevious
{
    /// <summary>Its version.</summary>
    public required string Version { get; init; }
    /// <summary>Its declarations' digest (<see cref="PackTrialGate.DigestOf"/>).</summary>
    public required string ManifestDigest { get; init; }
    /// <summary>For a pack, its digest; null for compiled code.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PackDigest { get; init; }
    /// <summary>What it measured on the same tasks.</summary>
    public required PackTrialMeasurement With { get; init; }
}

/// <summary>What one task of a trial offered, and how many of those items were in its truth.</summary>
[Experimental("AMEXT001")]
public sealed record PackTrialTask(int Offered, int Correct);

/// <summary>
/// One target checked: what was required, what the decision used (a confidence lower bound when the trial asks for one),
/// whether it held, and the plain estimate beside a bound.
/// </summary>
[Experimental("AMEXT001")]
public sealed record PackTrialCheck(string Target, double Required, double Observed, bool Met, double? Estimate = null);

/// <summary>
/// What a decision rests on when it is not the host's own trial (pack format v1.2 §16, certificate trust): an evaluation
/// certificate the host verified, from a key it trusts as evaluator for the pack's id, and chose to rely on. A host composes
/// such a decision only in namespaces where it trusts certificates (<see cref="ModuleSetBuilder.TrustCertificatesFor"/>);
/// everywhere else only its own trial counts (proven here).
/// </summary>
[Experimental("AMEXT001")]
public sealed record PackDecisionBasis
{
    /// <summary>The kind of basis this host used: <c>certificate</c>.</summary>
    public required string Kind { get; init; }
    /// <summary>The certificate relied on: <c>sha256:</c> over its envelope's exact bytes.</summary>
    public required string Certificate { get; init; }
    /// <summary>Who evaluated, as the certificate names it.</summary>
    public required string Evaluator { get; init; }
    /// <summary>The key that signed the certificate, trusted here as evaluator.</summary>
    public required string EvaluatorKey { get; init; }
    /// <summary>When the evaluator evaluated.</summary>
    public required DateTimeOffset EvaluatedAt { get; init; }
    /// <summary>Where the evaluator's tasks came from.</summary>
    public required string TaskSource { get; init; }
    /// <summary>The AEF run the certificate summarises: AEF's run id; null when it names none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AefRunId { get; init; }
    /// <summary>That run's run hash (AEF 1.0 SEAL-4); null when it names none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AefRunHash { get; init; }
}

/// <summary>
/// A host's decision about one pack, from its trial (pack format v1.1 §4, claim 2): on or off, the reason, and every check.
/// Bound to the exact declarations by <see cref="ManifestDigest"/> and, for a pack, to its exact content by
/// <see cref="PackDigest"/> (PACK-bind): a changed pack needs a new trial.
/// </summary>
[Experimental("AMEXT001")]
public sealed record PackActivation
{
    /// <summary>The pack's module id.</summary>
    public required string ModuleId { get; init; }
    /// <summary>Its version.</summary>
    public required string Version { get; init; }
    /// <summary>
    /// The declarations the trial ran: <c>jcs-sha256:</c>, SHA-256 over the manifest's RFC 8785 canonical JSON
    /// (<see cref="PackTrialGate.DigestOf"/>). Records written before PACK-bind name <c>sha256:</c> of the SDK's serialization,
    /// and match nothing now: those packs are trialled again.
    /// </summary>
    public required string ManifestDigest { get; init; }
    /// <summary>
    /// For a pack, the content the trial ran: <c>sha256:</c> over the exact bytes of its file list, the identity the ledger and
    /// a composite's lock name. Null for a module compiled into the host; a decision for one never counts for the other.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PackDigest { get; init; }
    /// <summary>Whether every target was met.</summary>
    public required bool Active { get; init; }
    /// <summary>Why, in one line.</summary>
    public required string Reason { get; init; }
    /// <summary>When the host decided.</summary>
    public required DateTimeOffset DecidedAt { get; init; }
    /// <summary>Every target checked.</summary>
    public IReadOnlyList<PackTrialCheck> Checks { get; init; } = [];
    /// <summary>The arm with the pack.</summary>
    public PackTrialMeasurement? With { get; init; }
    /// <summary>The arm without it.</summary>
    public PackTrialMeasurement? Without { get; init; }
    /// <summary>For an upgrade, the active version it was compared against on the same tasks; null otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PackTrialPrevious? Previous { get; init; }
    /// <summary>
    /// The upgrade rule the trial declared, recorded with the decision: the next version is held to it even if it declares a
    /// looser one, or none (red team 6a).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModuleTrialUpgrade? Upgrade { get; init; }

    /// <summary>The confidence the trial declared, so the upgrade chain checks no regression with the same bounds (review 2).</summary>
    public double? Confidence { get; init; }

    /// <summary>
    /// How the trial drew its tasks: <c>parent</c> (each at its commit's parent, as it stood before the change), <c>head</c> (the
    /// repository as it is now, which sees the future) or null when the runner did not say (review 2).
    /// </summary>
    public string? History { get; init; }
    /// <summary>
    /// The statistic the bounds were computed with (<see cref="PackTrialGate.Statistic"/>), or null for a record from before it
    /// was kept (review 4). An upgrade whose record names another one is not compared again with the current formula: its
    /// verdict would change with the library, not with a trial.
    /// </summary>
    public string? Statistic { get; init; }
    /// <summary>
    /// What the decision rests on when it is not this host's own trial: an evaluation certificate (certificate trust). Null for
    /// a decision from the host's own trial, which every namespace accepts.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PackDecisionBasis? Basis { get; init; }
}

/// <summary>The activation records a host has, by pack.</summary>
[Experimental("AMEXT001")]
public interface IPackActivations
{
    /// <summary>The latest decision for this pack id and version, or null when it was never trialled here.</summary>
    PackActivation? Find(string moduleId, ModuleVersion version);

    /// <summary>
    /// The decision that counts for exactly this content (id, version, manifest digest and, for a pack, its pack digest; null
    /// for a compiled module), by the gate's own rule: this host's latest own decision whenever it has one, otherwise its
    /// latest decision made from a certificate; null when it was never decided.
    /// </summary>
    PackActivation? Find(string moduleId, ModuleVersion version, string manifestDigest, string? packDigest = null) =>
        PackTrialGate.Counting(History(moduleId).Where(a => a.Version == version.ToString() && a.ManifestDigest == manifestDigest && a.PackDigest == packDigest),
            certificatesCount: true);

    /// <summary>
    /// Every decision for this pack id, any version: what the gate decides from (review 1, M7: required, so an implementation
    /// that leaves it out does not silently switch every trialled pack off).
    /// </summary>
    IReadOnlyList<PackActivation> History(string moduleId);
}

/// <summary>A module the host left off, and why.</summary>
[Experimental("AMEXT001")]
public sealed record SwitchedOffModule(string ModuleId, string Version, string Reason);

/// <summary>Decides a trial, and applies decisions when a module set activates.</summary>
[Experimental("AMEXT001")]
public static class PackTrialGate
{
    /// <summary>
    /// The statistic this library computes bounds with, as a decision records it: the precision bounds of review 4 (leave-one-task-out
    /// variances, Student's t). Changing a formula changes this name.
    /// </summary>
    public const string Statistic = "agentmemory.trial.bounds/2026-10-29";

    /// <summary>The one-sided normal quantiles the trial's confidence may name.</summary>
    internal static readonly IReadOnlyDictionary<double, double> Confidences = new Dictionary<double, double>
    {
        [0.9] = 1.2815516, [0.95] = 1.6448536, [0.975] = 1.9599640, [0.99] = 2.3263479,
    };

    /// <summary>The Wilson score lower bound of <paramref name="successes"/> out of <paramref name="trials"/> at quantile <paramref name="z"/>.</summary>
    public static double WilsonLower(int successes, int trials, double z)
    {
        if (trials <= 0) return 0;
        var p = (double)successes / trials;
        var z2 = z * z;
        var centre = p + z2 / (2.0 * trials);
        var margin = z * Math.Sqrt(p * (1 - p) / trials + z2 / (4.0 * trials * trials));
        return Math.Max(0, (centre - margin) / (1 + z2 / trials));
    }

    /// <summary>The Wilson score upper bound, the mirror of <see cref="WilsonLower(int, int, double)"/>.</summary>
    public static double WilsonUpper(int successes, int trials, double z) => trials <= 0 ? 1 : 1 - WilsonLower(trials - successes, trials, z);

    /// <summary>
    /// The lower confidence bound of a difference of hit rates measured on the same tasks (review 1, M4): Newcombe's method 10
    /// for paired proportions, from each rate's Wilson bounds and the correlation of the two arms task by task. Without
    /// per-task results it takes the arms as independent (Newcombe's method 10 for unpaired proportions), which is wider.
    /// </summary>
    public static double HitDifferenceLower(PackTrialMeasurement first, PackTrialMeasurement second, double z)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        var (p1, p2) = (first.HitRate, second.HitRate);
        var (l1, u2) = (WilsonLower(first.Hits, first.Tasks, z), WilsonUpper(second.Hits, second.Tasks, z));
        var phi = 0.0;
        if (first.PerTask is { } a && second.PerTask is { } b && a.Count == b.Count && a.Count == first.Tasks && first.Tasks == second.Tasks)
        {
            // The 2x2 table of hits on the same tasks: both, the first only, the second only, neither.
            int both = 0, firstOnly = 0, secondOnly = 0, neither = 0;
            for (var i = 0; i < a.Count; i++)
            {
                var (hit1, hit2) = (a[i].Correct > 0, b[i].Correct > 0);
                if (hit1 && hit2) both++; else if (hit1) firstOnly++; else if (hit2) secondOnly++; else neither++;
            }
            var denominator = Math.Sqrt((double)(both + firstOnly) * (secondOnly + neither) * (both + secondOnly) * (firstOnly + neither));
            phi = denominator == 0 ? 0 : ((double)both * neither - (double)firstOnly * secondOnly) / denominator;
        }
        return p1 - p2 - Math.Sqrt(Math.Max(0, Math.Pow(p1 - l1, 2) - 2 * phi * (p1 - l1) * (u2 - p2) + Math.Pow(u2 - p2, 2)));
    }

    /// <summary>
    /// The lower confidence bound of a difference of precisions (items right over items offered) measured on the same tasks,
    /// each task one cluster (review 4): Newcombe's construction from each arm's limit at its effective sample size (the Wilson
    /// bound at the design effect a leave-one-task-out variance gives) and the correlation of the two arms task by task, and
    /// never above the paired bound from the leave-one-task-out variance of the difference; both at Student's t with one
    /// degree of freedom fewer than the tasks either arm offered something on. Against an arm that offered nothing, the other arm's own bound
    /// (<see cref="ClusteredWilsonLower"/>), the one its precision target uses. Without per-task results, plain Wilson limits
    /// and no correlation.
    /// </summary>
    /// <remarks>
    /// By simulation (pack format v1.2 §7): a true difference equal to the target passes at most about 5% of the time at 95% in
    /// most regimes, and up to 8.5% where every item of a task is right or wrong together and a few tasks offer many items;
    /// the cluster-robust variance it replaces passed up to 16% there (review 4, M-1). Power on 40 tasks for a lift of 0.15
    /// against a target of 0.05: 0.25 to 0.75.
    /// </remarks>
    public static double PrecisionDifferenceLower(PackTrialMeasurement first, PackTrialMeasurement second, double z)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (second.Offered == 0)
            return first.Offered == 0 ? 0 : first.PerTask is { Count: > 0 } alone ? ClusteredWilsonLower(alone, z) : WilsonLower(first.Correct, first.Offered, z);
        if (first.Offered == 0)
            return -(second.PerTask is { Count: > 0 } other ? ClusteredWilsonUpper(other, z) : WilsonUpper(second.Correct, second.Offered, z));
        var (r1, r2) = (first.Precision, second.Precision);
        var (l1, u2) = (WilsonLower(first.Correct, first.Offered, z), WilsonUpper(second.Correct, second.Offered, z));
        if (first.PerTask is not { } a || second.PerTask is not { } b || a.Count != b.Count || a.Count < 2)
            return r1 - r2 - Math.Sqrt(Math.Pow(r1 - l1, 2) + Math.Pow(u2 - r2, 2));
        var n = a.Count;
        // Degrees of freedom: the tasks either arm offered something on, less one (a task neither answered says nothing).
        var q = StudentT(z, a.Zip(b).Count(p => p.First.Offered > 0 || p.Second.Offered > 0) - 1);
        l1 = EffectiveWilsonLower(a, q);
        u2 = 1 - EffectiveWilsonLower(Flipped(b), q);
        var (o1, o2) = (a.Sum(t => (double)t.Offered), b.Sum(t => (double)t.Offered));
        double Residual(PackTrialTask t, double r, double o) => (t.Correct - r * t.Offered) / o;
        var v1 = a.Sum(t => Math.Pow(Residual(t, r1, o1), 2));
        var v2 = b.Sum(t => Math.Pow(Residual(t, r2, o2), 2));
        var covariance = a.Zip(b).Sum(p => Residual(p.First, r1, o1) * Residual(p.Second, r2, o2));
        var rho = v1 > 0 && v2 > 0 ? Math.Clamp(covariance / Math.Sqrt(v1 * v2), -1, 1) : 0;
        var newcombe = r1 - r2 - Math.Sqrt(Math.Max(0, Math.Pow(r1 - l1, 2) - 2 * rho * (r1 - l1) * (u2 - r2) + Math.Pow(u2 - r2, 2)));
        // The paired bound: each task left out in turn, the spread of the difference that remains (review 4: the cluster-robust
        // sum of squared residuals ran low where a few tasks carry most items).
        var (left1, left2) = (LeftOut(a), LeftOut(b));
        var differences = left1.Zip(left2, (x, y) => x - y).ToList();
        var mean = differences.Average();
        var paired = r1 - r2 - q * Math.Sqrt((n - 1.0) / n * differences.Sum(d => Math.Pow(d - mean, 2)));
        // The lower of the two (review 3): Newcombe's never collapses when every task's precision is the same, and the paired
        // bound stays honest when the arms are strongly correlated and each task's items are all right or all wrong.
        return Math.Min(newcombe, paired);
    }

    /// <summary>The upper bound mirroring <see cref="ClusteredWilsonLower"/>: one minus the lower bound of the items that were wrong.</summary>
    public static double ClusteredWilsonUpper(IReadOnlyList<PackTrialTask> tasks, double z)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return tasks.Sum(t => t.Offered) == 0 ? 1 : 1 - ClusteredWilsonLower(Flipped(tasks), z);
    }

    /// <summary>
    /// The lower confidence bound of a ratio of items over tasks, with each task as one cluster (PACK-gate-stats-2, review 4):
    /// the lower of two bounds, both at Student's t with one degree of freedom fewer than the tasks that offered anything. Over items: the
    /// leave-one-task-out variance of the ratio gives a design effect, the item count divided by it is the effective sample
    /// size, and the Wilson bound is taken at that size. Over tasks: each task that offered anything is one observation scored
    /// by its own precision, and the Wilson bound is taken over them, which is the honest bound when every item of a task is
    /// right or wrong together. The second keeps a few tasks from passing for many however many items one of them offered,
    /// and keeps the bound monotone where every item is right (third review: two tasks gave 0.425 at 200 of 200 right and
    /// 0.978 at 199). Without it, where tasks are all right or all wrong and precision is high, a true precision equal to the
    /// target passed up to 21% of the time at 95% (review 4).
    /// </summary>
    public static double ClusteredWilsonLower(IReadOnlyList<PackTrialTask> tasks, double z)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var (items, overTasks) = ClusteredBounds(tasks, StudentT(z, tasks.Count(t => t.Offered > 0) - 1));
        return items is { } bound ? Math.Min(bound, overTasks) : overTasks;
    }

    // The two bounds of ClusteredWilsonLower at quantile q: over items at the effective size (null where no design effect can
    // be measured: one task answered, or a precision of 0 or 1, leaves no spread between tasks), and over tasks.
    private static (double? Items, double OverTasks) ClusteredBounds(IReadOnlyList<PackTrialTask> tasks, double q)
    {
        var offered = tasks.Sum(t => (double)t.Offered);
        if (offered == 0) return (null, 0);
        var p = tasks.Sum(t => (double)t.Correct) / offered;
        var answered = tasks.Where(t => t.Offered > 0).ToList();
        var overTasks = WilsonLower(answered.Average(t => (double)t.Correct / t.Offered), answered.Count, q);
        if (answered.Count < 2 || p is 0 or 1) return (null, overTasks);
        var left = LeftOut(tasks);
        var mean = left.Average();
        var jackknife = (tasks.Count - 1.0) / tasks.Count * left.Sum(r => Math.Pow(r - mean, 2));
        var designEffect = Math.Max(1, jackknife / (p * (1 - p) / offered));
        return (WilsonLower(p, offered / designEffect, q), overTasks);
    }

    // The pooled precision with each task left out in turn; a task whose removal leaves nothing keeps the whole estimate.
    private static List<double> LeftOut(IReadOnlyList<PackTrialTask> tasks)
    {
        var (offered, correct) = (tasks.Sum(t => (double)t.Offered), tasks.Sum(t => (double)t.Correct));
        var whole = offered == 0 ? 0 : correct / offered;
        return [.. tasks.Select(t => offered - t.Offered > 0 ? (correct - t.Correct) / (offered - t.Offered) : whole)];
    }

    private static List<PackTrialTask> Flipped(IReadOnlyList<PackTrialTask> tasks) => [.. tasks.Select(t => t with { Correct = t.Offered - t.Correct })];

    // One arm's limit for Newcombe's construction in a difference (review 3, 4): the bound over items at the effective size,
    // without the floor over tasks, which bounds the mean of per-task precisions, another quantity, and in a difference made
    // the bound far too strict where tasks offer few items (a true lift of 0 passed 1% of the time at a nominal 5%). The paired
    // structure keeps the difference honest where the floor was needed for one arm. Where no design effect can be measured,
    // the bound over tasks.
    private static double EffectiveWilsonLower(IReadOnlyList<PackTrialTask> tasks, double q)
    {
        var (items, overTasks) = ClusteredBounds(tasks, q);
        return items ?? overTasks;
    }

    /// <summary>
    /// Student's t quantile for one of the trial's confidences (its normal quantile <paramref name="z"/>) at the given degrees
    /// of freedom: from a table for 1 to 60 (exact to six decimals), the Cornish-Fisher expansion beyond (within 5e-7).
    /// </summary>
    internal static double StudentT(double z, int degrees)
    {
        var v = Math.Max(1, degrees);
        if (v <= 60)
            foreach (var (key, row) in StudentQuantiles)
                if (Math.Abs(key - z) < 1e-6) return row[v - 1];
        var (z3, z5, z7) = (Math.Pow(z, 3), Math.Pow(z, 5), Math.Pow(z, 7));
        return z + (z3 + z) / (4.0 * v) + (5 * z5 + 16 * z3 + 3 * z) / (96.0 * v * v) + (3 * z7 + 19 * z5 + 17 * z3 - 15 * z) / (384.0 * v * v * v);
    }

    // Student's t quantiles at the four confidences, for 1 to 60 degrees of freedom (computed with mpmath, 30 digits).
    private static readonly IReadOnlyDictionary<double, double[]> StudentQuantiles = new Dictionary<double, double[]>
    {
        [1.2815516] = [3.077684, 1.885618, 1.637744, 1.533206, 1.475884, 1.439756, 1.414924, 1.396815, 1.383029, 1.372184, 1.363430, 1.356217, 1.350171, 1.345030, 1.340606, 1.336757, 1.333379, 1.330391, 1.327728, 1.325341, 1.323188, 1.321237, 1.319460, 1.317836, 1.316345, 1.314972, 1.313703, 1.312527, 1.311434, 1.310415, 1.309464, 1.308573, 1.307737, 1.306952, 1.306212, 1.305514, 1.304854, 1.304230, 1.303639, 1.303077, 1.302543, 1.302035, 1.301552, 1.301090, 1.300649, 1.300228, 1.299825, 1.299439, 1.299069, 1.298714, 1.298373, 1.298045, 1.297730, 1.297426, 1.297134, 1.296853, 1.296581, 1.296319, 1.296066, 1.295821],
        [1.6448536] = [6.313752, 2.919986, 2.353363, 2.131847, 2.015048, 1.943180, 1.894579, 1.859548, 1.833113, 1.812461, 1.795885, 1.782288, 1.770933, 1.761310, 1.753050, 1.745884, 1.739607, 1.734064, 1.729133, 1.724718, 1.720743, 1.717144, 1.713872, 1.710882, 1.708141, 1.705618, 1.703288, 1.701131, 1.699127, 1.697261, 1.695519, 1.693889, 1.692360, 1.690924, 1.689572, 1.688298, 1.687094, 1.685954, 1.684875, 1.683851, 1.682878, 1.681952, 1.681071, 1.680230, 1.679427, 1.678660, 1.677927, 1.677224, 1.676551, 1.675905, 1.675285, 1.674689, 1.674116, 1.673565, 1.673034, 1.672522, 1.672029, 1.671553, 1.671093, 1.670649],
        [1.9599640] = [12.706205, 4.302653, 3.182446, 2.776445, 2.570582, 2.446912, 2.364624, 2.306004, 2.262157, 2.228139, 2.200985, 2.178813, 2.160369, 2.144787, 2.131450, 2.119905, 2.109816, 2.100922, 2.093024, 2.085963, 2.079614, 2.073873, 2.068658, 2.063899, 2.059539, 2.055529, 2.051831, 2.048407, 2.045230, 2.042272, 2.039513, 2.036933, 2.034515, 2.032245, 2.030108, 2.028094, 2.026192, 2.024394, 2.022691, 2.021075, 2.019541, 2.018082, 2.016692, 2.015368, 2.014103, 2.012896, 2.011741, 2.010635, 2.009575, 2.008559, 2.007584, 2.006647, 2.005746, 2.004879, 2.004045, 2.003241, 2.002465, 2.001717, 2.000995, 2.000298],
        [2.3263479] = [31.820516, 6.964557, 4.540703, 3.746947, 3.364930, 3.142668, 2.997952, 2.896459, 2.821438, 2.763769, 2.718079, 2.680998, 2.650309, 2.624494, 2.602480, 2.583487, 2.566934, 2.552380, 2.539483, 2.527977, 2.517648, 2.508325, 2.499867, 2.492159, 2.485107, 2.478630, 2.472660, 2.467140, 2.462021, 2.457262, 2.452824, 2.448678, 2.444794, 2.441150, 2.437723, 2.434494, 2.431447, 2.428568, 2.425841, 2.423257, 2.420803, 2.418470, 2.416250, 2.414134, 2.412116, 2.410188, 2.408345, 2.406581, 2.404892, 2.403272, 2.401718, 2.400225, 2.398790, 2.397410, 2.396081, 2.394801, 2.393568, 2.392377, 2.391229, 2.390119],
    };

    // The Wilson score lower bound at proportion p over a (possibly fractional) effective sample size n.
    private static double WilsonLower(double p, double n, double z)
    {
        if (n <= 0) return 0;
        var z2 = z * z;
        var centre = p + z2 / (2.0 * n);
        var margin = z * Math.Sqrt(p * (1 - p) / n + z2 / (4.0 * n * n));
        return Math.Max(0, (centre - margin) / (1 + z2 / n));
    }

    /// <summary>The prefix of <see cref="DigestOf"/>: SHA-256 over RFC 8785 canonical JSON, named apart from a pack's <c>sha256:</c>.</summary>
    public const string ManifestDigestPrefix = "jcs-sha256:";

    /// <summary>
    /// The declarations' digest an activation record binds to: <c>jcs-sha256:</c> and the SHA-256 of the manifest's RFC 8785
    /// canonical JSON, so the bytes do not depend on how this SDK orders or spaces what it writes.
    /// </summary>
    /// <remarks>
    /// The JSON is this SDK's serialization of the manifest, not the bytes a pack declares, so its names and defaults are part of
    /// the digest (review 1, M17). Changing it switches off every trialled pack on every host until trialled again, so a golden
    /// vector pins it (<c>TrialGateTests.TheDeclarationsDigestIsPinned</c>); a change needs a migration that accepts the old
    /// digest for a release. Hashing the declared files instead is the 2.0 direction.
    /// </remarks>
    public static string DigestOf(ModuleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return ManifestDigestPrefix + Convert.ToHexString(SHA256.HashData(CanonicalJson.Canonical(JsonSerializer.SerializeToUtf8Bytes(manifest)))).ToLowerInvariant();
    }

    private static readonly Regex PackDigestPattern = new("^sha256:[0-9a-f]{64}\\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// Checks every target of the manifest's trial against the two arms; the pack goes on only if all are met. Give
    /// <paramref name="packDigest"/> when the trial ran a pack, so the decision binds to its content and not only to its declarations.
    /// </summary>
    /// <param name="manifest">The declarations under trial.</param>
    /// <param name="with">The arm with the pack.</param>
    /// <param name="without">The arm without it: the baseline.</param>
    /// <param name="decidedAt">When the host decided.</param>
    /// <param name="packDigest">For a pack, its digest; null for compiled code.</param>
    /// <param name="previous">For an upgrade, the version that passed on this host before, measured on the same tasks; with
    /// <c>upgrade.noRegression</c>, its hit rate and precision become two more targets.</param>
    /// <exception cref="ArgumentException">The manifest declares no trial, <paramref name="packDigest"/> is not <c>sha256:</c> and 64 hex
    /// digits, or <paramref name="previous"/> is not an earlier version measured on as many tasks.</exception>
    public static PackActivation Decide(ModuleManifest manifest, PackTrialMeasurement with, PackTrialMeasurement without, DateTimeOffset decidedAt,
        string? packDigest = null, PackTrialPrevious? previous = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (packDigest is not null && !PackDigestPattern.IsMatch(packDigest))
            throw new ArgumentException("A pack digest is sha256: and 64 lowercase hex digits.", nameof(packDigest));
        ArgumentNullException.ThrowIfNull(with);
        ArgumentNullException.ThrowIfNull(without);
        // Review 1, M4: two arms of one trial ran the same tasks, and no arm hits more tasks than it ran or is right more often
        // than it offered (as a certificate requires of the same numbers).
        if (without.Tasks != with.Tasks) throw new ArgumentException("Both arms run the same tasks.", nameof(without));
        foreach (var arm in new[] { with, without })
            if (arm.Tasks < 0 || arm.Hits < 0 || arm.Hits > arm.Tasks || arm.Offered < 0 || arm.Correct < 0 || arm.Correct > arm.Offered)
                throw new ArgumentException("An arm hits at most its tasks and is right at most as often as it offered.", nameof(with));
        if (without.PerTask is { } baselineTasks && (baselineTasks.Count != without.Tasks || baselineTasks.Sum(t => t.Offered) != without.Offered
                || baselineTasks.Sum(t => t.Correct) != without.Correct || baselineTasks.Any(t => t.Correct < 0 || t.Correct > t.Offered)))
            throw new ArgumentException("PerTask holds one entry per task, 0 <= correct <= offered, summing to Offered and Correct.", nameof(without));
        if (with.PerTask is { } perTask && (perTask.Count != with.Tasks || perTask.Sum(t => t.Offered) != with.Offered
                || perTask.Sum(t => t.Correct) != with.Correct || perTask.Any(t => t.Correct < 0 || t.Correct > t.Offered)))
            throw new ArgumentException("PerTask holds one entry per task, 0 <= correct <= offered, summing to Offered and Correct.", nameof(with));
        // The previous version's measurement is checked as the two arms are (review 4: inconsistent numbers gave a wrong bound,
        // which was then signed).
        if (previous?.With is { } earlier && (earlier.Tasks < 0 || earlier.Hits < 0 || earlier.Hits > earlier.Tasks || earlier.Offered < 0
                || earlier.Correct < 0 || earlier.Correct > earlier.Offered || earlier.PerTask is { } earlierTasks && (earlierTasks.Count != earlier.Tasks
                || earlierTasks.Sum(t => t.Offered) != earlier.Offered || earlierTasks.Sum(t => t.Correct) != earlier.Correct
                || earlierTasks.Any(t => t.Correct < 0 || t.Correct > t.Offered))))
            throw new ArgumentException("The previous version's measurement hits at most its tasks, is right at most as often as it offered, and its PerTask sums to it.", nameof(previous));
        // The previous version is earlier, or the same version with other content (a replacement is an upgrade too).
        if (previous is not null && (!ModuleVersion.TryParse(previous.Version, out var before) || before.CompareTo(manifest.Version) > 0
                || before.CompareTo(manifest.Version) == 0 && previous.ManifestDigest == DigestOf(manifest) && previous.PackDigest == packDigest
                || previous.With.Tasks != with.Tasks))
            throw new ArgumentException("The previous version is an earlier version, or other content under this one, measured on the same tasks.", nameof(previous));
        var trial = manifest.Trial ?? throw new ArgumentException($"Module '{manifest.Id}' declares no trial.", nameof(manifest));
        var checks = new List<PackTrialCheck>();
        var t = trial.Targets;
        double? z = trial.Confidence is { } c ? Confidences[c] : null;
        // A rate target meets its bound when the trial asks for confidence, its estimate otherwise.
        PackTrialCheck Rate(string name, double required, int successes, int trials)
        {
            var estimate = trials == 0 ? 0 : (double)successes / trials;
            if (z is not { } quantile) return new(name, required, Math.Round(estimate, 3), estimate >= required);
            var bound = WilsonLower(successes, trials, quantile);
            return new(name, required, Math.Round(bound, 3), bound >= required, Math.Round(estimate, 3));
        }
        // Precision with each task as a cluster: its items are correlated, so the bound is the cluster-robust one.
        static PackTrialCheck Clustered(string name, double required, double estimate, double bound) =>
            new(name, required, Math.Round(bound, 3), bound >= required, Math.Round(estimate, 3));
        if (t.HitRate is { } hit) checks.Add(Rate("hitRate", hit, with.Hits, with.Tasks));
        if (t.Precision is { } precision)
            checks.Add(z is { } q && with.PerTask is { } clustered
                ? Clustered("precision", precision, with.Precision, ClusteredWilsonLower(clustered, q))
                : Rate("precision", precision, with.Correct, with.Offered));
        // A difference target meets its bound when the trial asks for confidence, its estimate otherwise (review 1, M4): the
        // same promise the rates keep, so a lift on a few tasks cannot pass by luck either.
        PackTrialCheck Difference(string name, double required, double estimate, Func<double, double> lower)
        {
            if (z is not { } quantile) return new(name, required, Math.Round(estimate, 3), estimate >= required - 1e-9);
            var bound = lower(quantile);
            return new(name, required, Math.Round(bound, 3), bound >= required - 1e-9, Math.Round(estimate, 3));
        }
        if (t.Lift is { } lift) checks.Add(Difference("lift", lift, with.HitRate - without.HitRate, q => HitDifferenceLower(with, without, q)));
        if (t.PrecisionLift is { } precisionLift)
            checks.Add(Difference("precisionLift", precisionLift, with.Precision - without.Precision, q => PrecisionDifferenceLower(with, without, q)));
        // Latency is a budget the host sets, not a rate: its p95 is compared as measured.
        if (t.AddedP95Ms is { } added) checks.Add(new("addedP95Ms", added, Math.Round(with.P95Ms - without.P95Ms, 1), with.P95Ms - without.P95Ms <= added));
        // An upgrade does not do worse than the version it replaces, on the same tasks, beyond the declared tolerance.
        if (trial.Upgrade is { NoRegression: true } upgrade && previous is not null)
        {
            // With a declared confidence and a tolerance above 0, no regression is a non-inferiority test: the lower bound
            // clears -tolerance. At tolerance 0 no measurement could show it, so the estimate must not drop (review 2: an
            // upgrade as good as its predecessor failed at the default tolerance).
            PackTrialCheck NoRegression(string name, double estimate, Func<double, double> lower) => upgrade.Tolerance > 0
                ? Difference(name, -upgrade.Tolerance, estimate, lower) : new(name, 0, Math.Round(estimate, 3), estimate >= -1e-9);
            checks.Add(NoRegression("noRegression.hitRate", with.HitRate - previous.With.HitRate, q => HitDifferenceLower(with, previous.With, q)));
            checks.Add(NoRegression("noRegression.precision", with.Precision - previous.With.Precision, q => PrecisionDifferenceLower(with, previous.With, q)));
        }
        var enough = with.Tasks >= ModuleTrial.MinimumTasks;
        var failed = checks.Where(c => !c.Met).ToList();
        var reason = !enough ? $"only {with.Tasks} tasks in this host's history; a trial needs {ModuleTrial.MinimumTasks}"
            : failed.Count == 0 ? "every target met"
            : string.Join("; ", failed.Select(c => c.Target == "addedP95Ms"
                ? $"{c.Target} {c.Observed.ToString(CultureInfo.InvariantCulture)} above {c.Required.ToString(CultureInfo.InvariantCulture)}"
                : $"{c.Target} {c.Observed.ToString(CultureInfo.InvariantCulture)} below {c.Required.ToString(CultureInfo.InvariantCulture)}"));
        return new PackActivation
        {
            ModuleId = manifest.Id, Version = manifest.Version.ToString(), ManifestDigest = DigestOf(manifest), PackDigest = packDigest,
            Active = enough && failed.Count == 0, Reason = reason, DecidedAt = decidedAt, Checks = checks, With = with, Without = without,
            Previous = previous, Upgrade = trial.Upgrade, Confidence = trial.Confidence, Statistic = trial.Confidence is null ? null : Statistic,
        };
    }

    /// <summary>
    /// The registrations that may compose, and the ones left off: a module that declares a trial needs a passing decision for
    /// exactly its manifest and, when it came from a pack, exactly that pack; a decision the host made from a certificate counts
    /// only in a namespace where it trusts certificates, and elsewhere its latest own trial decides; in a namespace where the
    /// host made trials mandatory, a module that declares none is left off; a module that needs one left off is left off too.
    /// </summary>
    internal static (IReadOnlyList<ModuleRegistration> On, IReadOnlyList<SwitchedOffModule> Off) Apply(IReadOnlyList<ModuleRegistration> registrations,
        IPackActivations? activations, IReadOnlyList<string>? mandatory = null, IReadOnlyList<string>? certificateTrust = null)
    {
        if (activations is null) return (registrations, []);
        var off = new Dictionary<string, SwitchedOffModule>(StringComparer.Ordinal);
        foreach (var registration in registrations.Where(r => r.Manifest.Trial is null))
            if ((mandatory ?? []).FirstOrDefault(p => ModuleSetBuilder.Covers(p, registration.Manifest.Id)) is { } prefix)
                off[registration.Manifest.Id] = new SwitchedOffModule(registration.Manifest.Id, registration.Manifest.Version.ToString(),
                    $"it declares no trial, and this host requires one for {prefix}");
        foreach (var registration in registrations.Where(r => r.Manifest.Trial is not null))
        {
            var manifest = registration.Manifest;
            var certificatesCount = (certificateTrust ?? []).Any(p => ModuleSetBuilder.Covers(p, manifest.Id));
            var decisions = Of(activations, registration);
            var decision = Counting(decisions, certificatesCount);
            var certified = decisions.Where(a => a.Basis is not null).MaxBy(a => a.DecidedAt);
            // Proven here unless the host chose otherwise for this namespace (review 1, M1; review 2, L3 and L5): a decision
            // made from a certificate counts only there, only for a pack added on its own, and only while that certificate is
            // the latest trusted one the pack carries. Otherwise the pack needs its own trial.
            string? setAside = null;
            if (decision?.Basis is { } basis && registration.TrustedCertificates?.Contains(basis.Certificate, StringComparer.Ordinal) is not true)
            {
                setAside = registration.TrustedCertificates is null
                    ? $"it was admitted on {basis.Evaluator}'s certificate, but it is composed as part of a family or as compiled code, where this host relies on its own trials only"
                    : $"it was admitted on {basis.Evaluator}'s certificate, which is no longer the latest trusted certificate it carries (or no longer travels with it, or is no longer trusted here); admit it again or trial it here";
                decision = null;
            }
            else if (decision is null && certified?.Basis is { } elsewhere)
                setAside = $"it was admitted on {elsewhere.Evaluator}'s certificate, but this host trusts certificates only where it chooses; here it needs its own trial";
            var reason = decision is not null ? (decision.Active ? Upgrade(activations, manifest, decision, certificatesCount) : "its trial failed: " + decision.Reason)
                : setAside is not null ? setAside
                : activations.Find(manifest.Id, manifest.Version) is not null ? "its trial ran on other content; trial it again"
                : "no trial on this host yet";
            if (reason is not null) off[manifest.Id] = new SwitchedOffModule(manifest.Id, manifest.Version.ToString(), reason);
        }
        for (var changed = off.Count > 0; changed;)
        {
            changed = false;
            foreach (var registration in registrations.Where(r => !off.ContainsKey(r.Manifest.Id)))
                if (registration.Manifest.Modules.Keys.FirstOrDefault(off.ContainsKey) is { } needed)
                {
                    off[registration.Manifest.Id] = new SwitchedOffModule(registration.Manifest.Id, registration.Manifest.Version.ToString(),
                        $"it needs {needed}, which is off");
                    changed = true;
                }
        }
        return ([.. registrations.Where(r => !off.ContainsKey(r.Manifest.Id))], [.. off.Values.OrderBy(o => o.ModuleId, StringComparer.Ordinal)]);
    }

    // PACK-upgrade-gate: a passing upgrade that must not regress goes on only if its trial compared it with exactly the version
    // that passed here before (the latest earlier version with a passing decision); null when it may go on.
    // Every decision this host signed for exactly what a registration composes: its manifest and, for a pack, its content.
    internal static IReadOnlyList<PackActivation> Of(IPackActivations activations, ModuleRegistration registration)
    {
        var manifestDigest = DigestOf(registration.Manifest);
        var version = registration.Manifest.Version.ToString();
        return [.. activations.History(registration.Manifest.Id).Where(a => a.Version == version && a.ManifestDigest == manifestDigest && a.PackDigest == registration.PackDigest)];
    }

    // Which of the host's decisions about exactly one content counts, the one rule the gate and the upgrade gate share
    // (review 2): the host's own latest decision whenever it has one, pass or fail, so a certificate never hides or overrules
    // what this host measured itself; otherwise, only where it trusts certificates, its latest decision made from one.
    internal static PackActivation? Counting(IEnumerable<PackActivation> decisions, bool certificatesCount)
    {
        var all = decisions as IReadOnlyCollection<PackActivation> ?? [.. decisions];
        return all.Where(a => a.Basis is null).MaxBy(a => a.DecidedAt) ?? (certificatesCount ? all.Where(a => a.Basis is not null).MaxBy(a => a.DecidedAt) : null);
    }

    // Outside certificate trust, a decision made from a certificate never counts as a version that passed here (review 1, M2).
    private static string? Upgrade(IPackActivations activations, ModuleManifest manifest, PackActivation decision, bool certificatesCount) =>
        Settle(activations, decision, decision.Upgrade ?? manifest.Trial?.Upgrade,
            new Dictionary<PackActivation, (string?, ModuleTrialUpgrade?)>(ReferenceEqualityComparer.Instance), certificatesCount).Refusal;

    // Why a passing decision may not go on as an upgrade (or null), and the rule it is held to: the stricter of its own and
    // the one its predecessor was held to, so a rule carries down the whole chain, not one version (third review). The version
    // it replaces is the one that passed here before it: of every content's current decision (a later failing one supersedes
    // a pass), a passing one that the gate itself allows, at an earlier version, or at this version with other content
    // decided earlier (on the same instant, the lower digest first); the highest, the latest.
    private static (string? Refusal, ModuleTrialUpgrade? Rule) Settle(IPackActivations activations, PackActivation decision, ModuleTrialUpgrade? rule,
        Dictionary<PackActivation, (string?, ModuleTrialUpgrade?)> known, bool certificatesCount)
    {
        if (known.TryGetValue(decision, out var settled)) return settled;
        known[decision] = ("it is its own predecessor", rule); // never reached: predecessors come strictly before
        var version = ModuleVersion.Parse(decision.Version);
        var content = Content(decision);
        // Review 1, M10: only the same kind of content stands before an upgrade: a pack after a pack, compiled code after compiled
        // code (a --variant run is compiled code, so it never becomes a pack's predecessor).
        var active = activations.History(decision.ModuleId)
            .Where(a => (a.PackDigest is null) == (decision.PackDigest is null))
            .GroupBy(a => (a.Version, a.ManifestDigest, a.PackDigest))
            .Select(g => Counting(g, certificatesCount))
            .OfType<PackActivation>()
            .Select(a => (Decision: a, Version: ModuleVersion.TryParse(a.Version, out var v) ? v : null))
            .Where(a => a.Decision.Active && a.Version is not null && Content(a.Decision) != content
                && (a.Version.CompareTo(version) < 0 || a.Version.CompareTo(version) == 0
                    && (a.Decision.DecidedAt < decision.DecidedAt || a.Decision.DecidedAt == decision.DecidedAt && string.CompareOrdinal(Content(a.Decision), content) < 0)))
            .OrderByDescending(a => a.Version).ThenByDescending(a => a.Decision.DecidedAt).ThenByDescending(a => Content(a.Decision), StringComparer.Ordinal)
            .Select(a => (a.Decision, Settled: Settle(activations, a.Decision, a.Decision.Upgrade, known, certificatesCount)))
            .FirstOrDefault(a => a.Settled.Refusal is null);
        var held = active.Decision is null ? rule : Stricter(rule, active.Settled.Rule);
        var result = (active.Decision is null ? null : Against(decision, held, active.Decision), held);
        known[decision] = result;
        return result;
    }

    private static string Content(PackActivation decision) => (decision.PackDigest ?? "") + "|" + decision.ManifestDigest;

    // The stricter of two upgrade rules: no regression if either asks it, at the smaller tolerance.
    private static ModuleTrialUpgrade? Stricter(ModuleTrialUpgrade? a, ModuleTrialUpgrade? b)
    {
        var rules = new[] { a, b }.Where(r => r is { NoRegression: true }).ToList();
        return rules.Count == 0 ? a ?? b : new ModuleTrialUpgrade { NoRegression = true, Tolerance = rules.Min(r => r!.Tolerance) };
    }

    // An upgrade against the version it replaces, held to the rule the chain carries, from the measurements the record
    // carries, whatever its own checks said.
    private static string? Against(PackActivation decision, ModuleTrialUpgrade? rule, PackActivation active)
    {
        if (rule is not { NoRegression: true }) return null;
        var tolerance = rule.Tolerance;
        if (decision.Previous is not { } previous)
            return $"it upgrades {active.Version}, which passed here; its trial must compare against that version";
        if (previous.Version != active.Version || previous.ManifestDigest != active.ManifestDigest || previous.PackDigest != active.PackDigest)
            return $"its trial compared against {previous.Version}, but {active.Version} ({Short(active.PackDigest ?? active.ManifestDigest)}) is the version that passed here";
        if (decision.With is not { } with) return "its record carries no measurement to compare";
        // The same test as the decision's own (review 2): bounds when it declared a confidence and the tolerance is above 0.
        double? z = decision.Confidence is { } c && Confidences.TryGetValue(c, out var q) && tolerance > 0 ? q : null;
        // Bounds from another statistic are not recomputed with this one (review 4): the upgrade waits for a trial under it.
        if (z is not null && decision.Statistic != Statistic)
            return $"its trial's bounds were computed with {decision.Statistic ?? "an earlier statistic"}, not {Statistic}: trial it again";
        var hitDrop = z is { } zh ? HitDifferenceLower(with, previous.With, zh) : with.HitRate - previous.With.HitRate;
        var precisionDrop = z is { } zp ? PrecisionDifferenceLower(with, previous.With, zp) : with.Precision - previous.With.Precision;
        return hitDrop >= -tolerance - 1e-9 && precisionDrop >= -tolerance - 1e-9 ? null
            : $"it does worse than {active.Version} beyond the tolerance {tolerance.ToString(CultureInfo.InvariantCulture)} (hit rate {Math.Round(hitDrop, 3).ToString(CultureInfo.InvariantCulture)}, "
              + $"precision {Math.Round(precisionDrop, 3).ToString(CultureInfo.InvariantCulture)})";
    }

    private static string Short(string digest) => digest[..Math.Min(digest.Length, digest.IndexOf(':', StringComparison.Ordinal) + 13)] + "…";
}

/// <summary>
/// Activation records kept in a folder as DSSE envelopes of in-toto statements, signed by the host's own key. A record whose
/// signature does not verify with <c>hostPublicKeyPem</c> is ignored, so editing a file cannot switch a pack on.
/// </summary>
[Experimental("AMEXT001")]
public sealed class PackActivationFolder : IPackActivations
{
    /// <summary>The statement's predicate type.</summary>
    public const string PredicateType = "urn:agentmemory:pack:trial:v1";
    private const string PayloadType = "application/vnd.in-toto+json";
    private readonly Dictionary<(string, string), PackActivation> _latest = [];
    private readonly Dictionary<string, List<PackActivation>> _history = new(StringComparer.Ordinal);
    private readonly Dictionary<PackActivation, string> _files = new(ReferenceEqualityComparer.Instance);

    /// <summary>Reads every <c>*.dsse.json</c> in <paramref name="folder"/>; files that do not verify are listed in <see cref="Ignored"/>.</summary>
    public PackActivationFolder(string folder, string hostPublicKeyPem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPublicKeyPem);
        var ignored = new List<string>();
        using var key = ECDsa.Create();
        key.ImportFromPem(hostPublicKeyPem);
        if (Directory.Exists(folder))
            foreach (var file in Directory.EnumerateFiles(folder, "*.dsse.json").Order(StringComparer.Ordinal))
            {
                var activation = Open(File.ReadAllBytes(file), key);
                if (activation is null) { ignored.Add(Path.GetFileName(file)); continue; }
                _files[activation] = file;
                var id = (activation.ModuleId, activation.Version);
                if (!_latest.TryGetValue(id, out var known) || known.DecidedAt < activation.DecidedAt) _latest[id] = activation;
                if (!_history.TryGetValue(activation.ModuleId, out var all)) _history[activation.ModuleId] = all = [];
                all.Add(activation);
            }
        Ignored = ignored.AsReadOnly();
    }

    /// <summary>Files that were not valid records signed by the host key.</summary>
    public IReadOnlyList<string> Ignored { get; }

    /// <summary>The signed record a decision was read from (PACK-evidence-aef: it travels with the decision's evidence); null for one read elsewhere.</summary>
    public string? FileOf(PackActivation activation) => _files.GetValueOrDefault(activation);

    /// <inheritdoc/>
    public PackActivation? Find(string moduleId, ModuleVersion version) => _latest.GetValueOrDefault((moduleId, version.ToString()));

    /// <inheritdoc/>
    public PackActivation? Find(string moduleId, ModuleVersion version, string manifestDigest, string? packDigest = null) =>
        PackTrialGate.Counting(History(moduleId).Where(a => a.Version == version.ToString() && a.ManifestDigest == manifestDigest && a.PackDigest == packDigest),
            certificatesCount: true);

    /// <inheritdoc/>
    public IReadOnlyList<PackActivation> History(string moduleId) => _history.TryGetValue(moduleId, out var all) ? all.AsReadOnly() : [];

    /// <summary>
    /// A version as it appears in a file name: itself up to 64 characters, else <c>v</c> and the first 16 hex digits of its
    /// SHA-256, so a long SemVer (up to 256 characters) never makes a name longer than a file system takes (review 2).
    /// </summary>
    public static string VersionInName(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.Length <= 64 ? version
            : "v" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(version)))[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Signs <paramref name="decision"/> with the host's key and writes it into <paramref name="folder"/> as a DSSE envelope of
    /// its in-toto statement, named by the pack, its version and the decision time; returns the file written.
    /// </summary>
    public static string Write(string folder, PackActivation decision, ECDsa hostKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(hostKey);
        Directory.CreateDirectory(folder);
        var statement = Statement(decision);
        var signature = hostKey.SignData(Pae(statement), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        var envelope = JsonSerializer.Serialize(new
        {
            payloadType = PayloadType, payload = Convert.ToBase64String(statement),
            signatures = new[] { new { keyid = "host", sig = Convert.ToBase64String(signature) } },
        });
        // Review 1, M5: the name holds the time to the tick and what was decided (the content's digest and the statement's), so
        // two decisions never share a file, even in the same second; the record appears whole or not at all (written aside,
        // then moved into place without overwriting), so a crash cannot leave a truncated record that silently drops a failure.
        var decided = decision.PackDigest ?? decision.ManifestDigest;
        var content = decided[(decided.IndexOf(':', StringComparison.Ordinal) + 1)..];
        var name = $"{decision.ModuleId}@{VersionInName(decision.Version)}-{decision.DecidedAt.UtcDateTime:yyyyMMddTHHmmssfffffffZ}-{content[..12]}-"
            + $"{Convert.ToHexString(SHA256.HashData(statement))[..8].ToLowerInvariant()}{(decision.Basis is null ? "" : "-certificate")}.dsse.json";
        var file = Path.Combine(folder, name);
        var partial = Path.Combine(folder, "." + name + "." + Guid.NewGuid().ToString("N") + ".partial");
        File.WriteAllText(partial, envelope);
        try { File.Move(partial, file, overwrite: false); }
        catch (IOException) when (File.Exists(file))
        {
            File.Delete(partial);
            // The very same decision, already recorded (by its statement: a signature differs each time): nothing to add.
            using var existing = JsonDocument.Parse(File.ReadAllBytes(file));
            if (!existing.RootElement.TryGetProperty("payload", out var payload) || payload.GetString() != Convert.ToBase64String(statement)) throw;
        }
        return file;
    }

    /// <summary>The DSSE pre-authentication encoding of a payload.</summary>
    public static byte[] Pae(ReadOnlySpan<byte> payload)
    {
        var head = Encoding.ASCII.GetBytes($"DSSEv1 {PayloadType.Length} {PayloadType} {payload.Length.ToString(CultureInfo.InvariantCulture)} ");
        return [.. head, .. payload];
    }

    /// <summary>
    /// The in-toto statement an activation record signs, as JSON bytes. Its subject is what the trial ran: the pack, by its
    /// digest, or for a compiled module its canonical manifest.
    /// </summary>
    public static byte[] Statement(PackActivation activation)
    {
        ArgumentNullException.ThrowIfNull(activation);
        return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["_type"] = "https://in-toto.io/Statement/v1",
            ["subject"] = new[] { new { name = activation.ModuleId + "@" + activation.Version, digest = new { sha256 = SubjectHex(activation) } } },
            ["predicateType"] = PredicateType,
            ["predicate"] = activation,
        });
    }

    private static PackActivation? Open(byte[] envelopeBytes, ECDsa key)
    {
        try
        {
            using var envelope = JsonDocument.Parse(envelopeBytes);
            var root = envelope.RootElement;
            if (root.GetProperty("payloadType").GetString() != PayloadType) return null;
            var payload = Convert.FromBase64String(root.GetProperty("payload").GetString()!);
            // Hashed once, at most sixteen entries tried, as for packs (sixth review).
            var signatures = root.GetProperty("signatures");
            if (signatures.GetArrayLength() > 16) return null;
            var hash = SHA256.HashData(Pae(payload));
            var verified = signatures.EnumerateArray().Any(s =>
                key.VerifyHash(hash, Convert.FromBase64String(s.GetProperty("sig").GetString()!), DSASignatureFormat.Rfc3279DerSequence));
            if (!verified) return null;
            using var statement = JsonDocument.Parse(payload);
            if (statement.RootElement.GetProperty("predicateType").GetString() != PredicateType) return null;
            var activation = statement.RootElement.GetProperty("predicate").Deserialize<PackActivation>();
            var subject = statement.RootElement.GetProperty("subject")[0];
            return activation is not null && subject.GetProperty("name").GetString() == activation.ModuleId + "@" + activation.Version
                && subject.GetProperty("digest").GetProperty("sha256").GetString() == SubjectHex(activation) ? activation : null;
        }
        catch (Exception e) when (e is JsonException or FormatException or KeyNotFoundException or InvalidOperationException or CryptographicException
            or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    // The statement's subject digest: the pack's when the trial ran a pack, otherwise the manifest's; hex after the prefix.
    private static string SubjectHex(PackActivation activation)
    {
        var digest = activation.PackDigest ?? activation.ManifestDigest;
        return digest[(digest.IndexOf(':', StringComparison.Ordinal) + 1)..];
    }
}
