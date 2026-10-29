using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
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
    /// <see cref="Offered"/> and <see cref="Correct"/>, one per task, and the tasks with a correct item are <see cref="Hits"/>.
    /// Null: a precision bound treats items as independent, and no paired difference can be bounded.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PackTrialTask>? PerTask { get; init; }
    /// <summary>
    /// The most items this arm could offer one task, fixed before it ran (the pack's <c>maxItems</c>; 3 for the grep baseline),
    /// 1 to 16; null is the format's most, 16. Every bound assumes one task the trial did not see, at this worst (review 5,
    /// root RB), so a smaller cap is a tighter bound; no task may offer more.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaximumOffered { get; init; }
    /// <summary>
    /// How far apart in the host's history the tasks were drawn: every <c>Spacing</c>-th eligible commit, 1 to 2,000 (review 6,
    /// H4: consecutive commits come in bursts of related changes, so they are not independent). Tasks closer than
    /// <see cref="PackTrialGate.BurstLength"/> count as one cluster in every bound, in runs of ⌈8 / spacing⌉ in the order
    /// drawn. Null is 1: tasks drawn one after another.
    /// </summary>
    public int? Spacing { get; init; }
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
    /// The statistic the recorded checks were computed with (<see cref="PackTrialGate.Statistic"/>), or null for a record from
    /// before it was kept (review 4). Informational: a host derives every verdict again from the evidence the record carries,
    /// under its own statistic, and can only take a pass back that way, never give one (review 5, M7).
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
    /// The statistic this library computes bounds with, as a decision records it (reviews 5 and 6, root RB). A hit rate's bound,
    /// and precision's when an arm offers one item a task at most, are Clopper-Pearson's, exact. Precision's and every
    /// difference's otherwise assume tasks the trial did not see, at the worst the arm's cap allows (they offer
    /// <see cref="PackTrialMeasurement.MaximumOffered"/> items, none right; for a difference, the other arm offers its cap, all
    /// right), counted <see cref="HedgeWeight"/> times beside the tasks it ran; then Fieller's test inversion of the pooled
    /// precision, or the paired difference task by task, at Student's t with one degree of freedom fewer than the tasks. The
    /// unseen tasks are the rule of three's lesson: a sample that never met a rare task with a large loss would otherwise pass
    /// as if there were none; their weight is the least that holds the nominal level for paired hits by exact enumeration
    /// (review 6: one unseen task was a rule of 2.7 where 3.0 is exact at 95%). Changing a formula changes this name; a
    /// recorded verdict is checked again under the current one (<see cref="Unmet"/>). Tasks drawn closer together in history
    /// than <see cref="BurstLength"/> eligible commits are clustered first: each run of them is one task of the bound (review 6,
    /// H4: drawn one after another, a hit-rate target at the truth passed 16 to 21% of the time at 95% when changes came in
    /// bursts of 5 to 8).
    /// </summary>
    public const string Statistic = "agentmemory.trial.hedged/3";

    /// <summary>
    /// The most eligible commits one burst of related changes is taken to span (review 6, H4), the one assumption about the
    /// order of a host's history: a trial whose tasks were drawn at least this far apart counts each as independent; closer,
    /// each run of ⌈8 / spacing⌉ consecutive tasks is one cluster. By the harness (eng/trial-statistics/serial.py), with bursts
    /// averaging 5 commits at an intraclass correlation of 0.3, a hit-rate target at the truth then passes at most the nominal
    /// rate (4.6% at 95%); averaging 8, whose longer bursts break the assumption, 6.4%.
    /// </summary>
    public const int BurstLength = 8;

    /// <summary>The largest spacing a measurement records: the runner draws from the last 2,000 commits.</summary>
    public const int MaximumSpacing = 2000;

    /// <summary>
    /// How many times the unseen worst-case task counts, by confidence: the least weight, in quarters, at which a paired hit
    /// difference equal to its target passes at most the nominal rate for every n from 10 to 200 and every pair of rates, by
    /// exact trinomial enumeration (eng/trial-statistics/hitdiff_exact.py: 9.4%, 4.6%, 2.1% and 0.85%); the one-arm and paired
    /// precision bounds hold with it too (the harness).
    /// </summary>
    internal static double HedgeWeight(double confidence) => confidence switch
    {
        0.9 => 1.25, 0.95 => 1.5, 0.975 => 1.75, 0.99 => 2.0,
        _ => throw new ArgumentOutOfRangeException(nameof(confidence), "A trial's confidence is 0.9, 0.95, 0.975 or 0.99."),
    };

    /// <summary>The one-sided normal quantiles the trial's confidence may name.</summary>
    internal static readonly IReadOnlyDictionary<double, double> Confidences = new Dictionary<double, double>
    {
        [0.9] = 1.2815516, [0.95] = 1.6448536, [0.975] = 1.9599640, [0.99] = 2.3263479,
    };

    /// <summary>
    /// The lower confidence bound of a rate of <paramref name="successes"/> out of <paramref name="trials"/>: Clopper-Pearson's,
    /// the rate at which as many successes or more have probability one minus <paramref name="confidence"/>. Exact for
    /// independent trials at any rate, 0 included (review 5: Wilson's ran hot near 0).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The confidence is not 0.9, 0.95, 0.975 or 0.99, or the counts are inconsistent.</exception>
    public static double RateLower(int successes, int trials, double confidence)
    {
        Quantile(confidence);
        if (trials < 0 || successes < 0 || successes > trials) throw new ArgumentOutOfRangeException(nameof(successes), "0 <= successes <= trials.");
        if (trials == 0 || successes == 0) return 0;
        var delta = 1 - confidence;
        var logChoose = new double[trials + 1];
        for (var j = 1; j <= trials; j++) logChoose[j] = logChoose[j - 1] + Math.Log(trials - j + 1) - Math.Log(j);
        double lo = 0, hi = 1;
        for (var step = 0; step < 64; step++)
        {
            var p = (lo + hi) / 2;
            var (lp, lq) = (Math.Log(p), double.LogP1(-p));
            var max = double.NegativeInfinity;
            for (var j = successes; j <= trials; j++) max = Math.Max(max, logChoose[j] + j * lp + (trials - j) * lq);
            var sum = 0.0;
            for (var j = successes; j <= trials; j++) sum += Math.Exp(logChoose[j] + j * lp + (trials - j) * lq - max);
            if (Math.Exp(max) * sum > delta) hi = p; else lo = p;
        }
        return lo;
    }

    /// <summary>
    /// The lower confidence bound of a pooled precision (items right over items offered) with each task one cluster: the
    /// unseen task, offering <paramref name="maximumOffered"/> items and none right, counted <see cref="HedgeWeight"/> times
    /// beside <paramref name="tasks"/>; then the least precision Fieller's test does not refuse, at Student's t with one degree
    /// of freedom fewer than the tasks. With a cap of one item a task, each offering task is one right-or-wrong trial and the
    /// bound is Clopper-Pearson's over them, exact (review 6: Fieller fell to a Wald bound there, above it). Order-free.
    /// </summary>
    public static double PrecisionLower(IReadOnlyList<PackTrialTask> tasks, int maximumOffered, double confidence)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var cap = Cap(maximumOffered);
        Within(tasks, cap);
        if (cap <= 1) return RateLower(tasks.Count(t => t.Correct > 0), tasks.Count(t => t.Offered > 0), confidence);
        return HedgedLower(tasks, cap, StudentT(Quantile(confidence), tasks.Count - 1), HedgeWeight(confidence));
    }

    /// <summary>The upper bound mirroring <see cref="PrecisionLower(IReadOnlyList{PackTrialTask}, int, double)"/>: one minus the lower bound of the items that were wrong.</summary>
    public static double PrecisionUpper(IReadOnlyList<PackTrialTask> tasks, int maximumOffered, double confidence)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return 1 - PrecisionLower([.. tasks.Select(t => t with { Correct = t.Offered - t.Correct })], maximumOffered, confidence);
    }

    /// <summary>
    /// The lower confidence bound of an arm's hit rate, as its trial is checked: Clopper-Pearson's over its tasks when they were
    /// drawn at least <see cref="BurstLength"/> apart; closer, over clusters of ⌈8 / spacing⌉ consecutive tasks, each a
    /// proportion of tasks hit, by the clustered bound (review 6, H4).
    /// </summary>
    /// <exception cref="ArgumentException">The tasks were drawn closer than <see cref="BurstLength"/> apart and the arm carries no per-task results.</exception>
    public static double HitRateLower(PackTrialMeasurement arm, double confidence)
    {
        ArgumentNullException.ThrowIfNull(arm);
        var m = ClusterSize(arm);
        if (m == 1) return RateLower(arm.Hits, arm.Tasks, confidence);
        var clusters = HitClusters(TasksOf(arm), m);
        return HedgedLower(clusters, m, StudentT(Quantile(confidence), clusters.Count - 1), HedgeWeight(confidence));
    }

    /// <summary>
    /// The lower confidence bound of an arm's pooled precision, as its trial is checked: <see cref="PrecisionLower(IReadOnlyList{PackTrialTask}, int, double)"/>
    /// over its tasks, or over clusters of ⌈8 / spacing⌉ consecutive tasks when they were drawn closer than
    /// <see cref="BurstLength"/> apart (review 6, H4).
    /// </summary>
    /// <exception cref="ArgumentException">The arm carries no per-task results.</exception>
    public static double PrecisionLower(PackTrialMeasurement arm, double confidence)
    {
        ArgumentNullException.ThrowIfNull(arm);
        var (m, cap, tasks) = (ClusterSize(arm), CapOf(arm), TasksOf(arm));
        if (m == 1) return PrecisionLower(tasks, cap, confidence);
        Within(tasks, cap);
        var clusters = Clusters(tasks, m);
        return HedgedLower(clusters, m * cap, StudentT(Quantile(confidence), clusters.Count - 1), HedgeWeight(confidence));
    }

    private static double PrecisionUpper(PackTrialMeasurement arm, double confidence) =>
        1 - PrecisionLower(arm with { PerTask = [.. TasksOf(arm).Select(t => t with { Correct = t.Offered - t.Correct })] }, confidence);

    /// <summary>
    /// The lower confidence bound of a difference of hit rates measured on the same tasks: the unseen task, the first arm
    /// missing it and the second hitting it, counted <see cref="HedgeWeight"/> times beside the per-task differences; then
    /// their mean at Student's t with one degree of freedom fewer than the tasks. Exact enumeration holds it at the nominal
    /// level for every n from 10 to 200 and every pair of rates.
    /// </summary>
    /// <exception cref="ArgumentException">Either arm has no per-task results, or they ran different tasks.</exception>
    public static double HitDifferenceLower(PackTrialMeasurement first, PackTrialMeasurement second, double confidence)
    {
        var (a, b) = Paired(first, second);
        // Drawn closer than a burst: the difference of two clustered hit proportions, paired cluster by cluster (review 6, H4).
        if (Math.Max(ClusterSize(first), ClusterSize(second)) is var m and > 1)
            return PairedDifference(HitClusters(a, m), HitClusters(b, m), m, m, confidence);
        var q = StudentT(Quantile(confidence), a.Count - 1);
        var d = new double[a.Count];
        for (var i = 0; i < a.Count; i++) d[i] = (a[i].Correct > 0 ? 1 : 0) - (b[i].Correct > 0 ? 1 : 0);
        var (mean, se) = Weighted(d, -1, HedgeWeight(confidence));
        return Math.Clamp(mean - q * se, -1, 1);
    }

    /// <summary>
    /// The lower confidence bound of a difference of pooled precisions measured on the same tasks: the unseen task, the first
    /// arm offering its cap with none right and the second its cap all right, counted <see cref="HedgeWeight"/> times beside
    /// the tasks; then the difference linearized task by task (each task's influence on both ratios), at Student's t with one
    /// degree of freedom fewer than the tasks. Against an arm that offers nothing by design (a cap of 0: the baseline
    /// <c>none</c>), the other arm's own bound, since nothing offered has no uncertainty; an arm that could offer but happened
    /// not to keeps its unseen task (review 6: a silent host baseline lost its hedge).
    /// </summary>
    /// <exception cref="ArgumentException">Either arm has no per-task results, or they ran different tasks.</exception>
    public static double PrecisionDifferenceLower(PackTrialMeasurement first, PackTrialMeasurement second, double confidence)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        var (cap1, cap2) = (CapOf(first), CapOf(second));
        if (cap2 == 0) return cap1 == 0 ? 0 : PrecisionLower(first, confidence);
        if (cap1 == 0) return -PrecisionUpper(second, confidence);
        var (a, b) = Paired(first, second);
        Within(a, cap1);
        Within(b, cap2);
        return Math.Max(ClusterSize(first), ClusterSize(second)) is var m and > 1
            ? PairedDifference(Clusters(a, m), Clusters(b, m), m * cap1, m * cap2, confidence)
            : PairedDifference(a, b, cap1, cap2, confidence);
    }

    // The paired difference of two pooled ratios over the same units (tasks, or clusters of them), each unit at most its arm's
    // cap: the unseen unit, the first arm offering its cap all wrong and the second its cap all right, counted HedgeWeight times.
    private static double PairedDifference(IReadOnlyList<PackTrialTask> a, IReadOnlyList<PackTrialTask> b, int cap1, int cap2, double confidence)
    {
        var q = StudentT(Quantile(confidence), a.Count - 1);
        var w = HedgeWeight(confidence);
        double o1 = w * cap1, c1 = 0, o2 = w * cap2, c2 = w * cap2;
        for (var i = 0; i < a.Count; i++) { o1 += a[i].Offered; c1 += a[i].Correct; o2 += b[i].Offered; c2 += b[i].Correct; }
        var n = a.Count + w;
        var (r1, r2, m1, m2) = (c1 / o1, c2 / o2, o1 / n, o2 / n);
        var f = new double[a.Count];
        for (var i = 0; i < a.Count; i++) f[i] = (a[i].Correct - r1 * a[i].Offered) / m1 - (b[i].Correct - r2 * b[i].Offered) / m2;
        var (_, se) = Weighted(f, (0 - r1 * cap1) / m1 - (cap2 - r2 * cap2) / m2, w);
        return Math.Clamp(r1 - r2 - q * se, -1, 1);
    }

    /// <summary>
    /// What a declared trial can never show at its task count and confidence, whatever the data (review 5, M4): each target
    /// that even the best evidence (every task hit, every item right, a baseline that gets nothing right) does not meet, and,
    /// for an upgrade rule, whether a version exactly as good as its predecessor can clear the tolerance. Each with the task
    /// count that would, when one within 200 does. Empty without a confidence: estimates promise nothing to fall short of.
    /// </summary>
    /// <param name="trial">The declared trial.</param>
    /// <param name="maximumOffered">The most items the pack offers a task (its <c>maxItems</c>), 1 to 16.</param>
    public static IReadOnlyList<string> Unreachable(ModuleTrial trial, int maximumOffered)
    {
        ArgumentNullException.ThrowIfNull(trial);
        if (trial.Confidence is not { } level || !Confidences.ContainsKey(level)) return [];
        var cap = Cap(maximumOffered);
        var t = trial.Targets;
        // The best evidence draws its tasks at least a burst apart (review 6, H4); a short history clusters them and shows less.
        PackTrialMeasurement Arm(int n, int offered, int right, int most) => new()
        {
            Tasks = n, Hits = right > 0 ? n : 0, Offered = n * offered, Correct = n * right, P95Ms = 0, MaximumOffered = most, Spacing = BurstLength,
            PerTask = [.. Enumerable.Repeat(new PackTrialTask(offered, right), n)],
        };
        // The best a trial of n tasks can show for each target; the baseline gets nothing right (grep offers its three, or nothing).
        var checks = new List<(string Target, double Required, Func<int, double> Best)>();
        if (t.HitRate is { } hit) checks.Add(("hitRate", hit, n => HitRateLower(Arm(n, cap, cap, cap), level)));
        if (t.Precision is { } precision) checks.Add(("precision", precision, n => PrecisionLower(Arm(n, cap, cap, cap), level)));
        PackTrialMeasurement Baseline(int n) => trial.Baseline == "host" ? Arm(n, 3, 0, 3) : Arm(n, 0, 0, 0);
        if (t.Lift is { } lift) checks.Add(("lift", lift, n => HitDifferenceLower(Arm(n, cap, cap, cap), Baseline(n), level)));
        if (t.PrecisionLift is { } precisionLift) checks.Add(("precisionLift", precisionLift, n => PrecisionDifferenceLower(Arm(n, cap, cap, cap), Baseline(n), level)));
        var notes = new List<string>();
        string Needed(Func<int, double> bound, double required) =>
            Enumerable.Range(trial.Tasks + 1, Math.Max(0, ModuleTrial.MaximumTasks - trial.Tasks)).FirstOrDefault(n => bound(n) >= required - 1e-9) is var n and > 0
                ? $"{n} tasks would" : $"no trial of up to {ModuleTrial.MaximumTasks} tasks would";
        foreach (var (target, required, best) in checks)
            if (best(trial.Tasks) is var bound && bound < required - 1e-9)
                notes.Add($"{target} {required.ToString(CultureInfo.InvariantCulture)} cannot be met on {trial.Tasks} tasks at {level.ToString(CultureInfo.InvariantCulture)}: "
                    + $"even the best evidence bounds it at {Math.Round(bound, 3).ToString(CultureInfo.InvariantCulture)}; {Needed(best, required)}.");
        if (trial.Upgrade is { NoRegression: true, Tolerance: > 0 } upgrade)
        {
            // Two versions that agree on every task: only the tasks the trial did not see separate them. Both checks, hit rate
            // and precision, at their best: every task offering the cap (review 6, M7: a pack that offers fewer items than its
            // cap needs more, since the unseen task offers the cap; on the code map's own forty tasks, -0.104 at a cap of 3).
            double Same(int n) => Math.Min(HitDifferenceLower(Arm(n, cap, 1, cap), Arm(n, cap, 1, cap), level),
                PrecisionDifferenceLower(Arm(n, cap, 1, cap), Arm(n, cap, 1, cap), level));
            if (Same(trial.Tasks) is var same && same < -upgrade.Tolerance - 1e-9)
                notes.Add($"An upgrade exactly as good as this version cannot clear the tolerance {upgrade.Tolerance.ToString(CultureInfo.InvariantCulture)} on {trial.Tasks} tasks: "
                    + $"agreeing on every task bounds the drop at {Math.Round(same, 3).ToString(CultureInfo.InvariantCulture)} at best; {Needed(Same, -upgrade.Tolerance)}, or a tolerance of {Math.Ceiling(-same * 100) / 100:0.00}. "
                    + $"That is when every task offers all {cap} items: a pack that offers fewer needs more, and a smaller maxItems tightens the bound.");
        }
        return notes;
    }

    // Fieller's lower bound of the pooled precision of the tasks and the unseen task of `cap` items, none right, counted w times:
    // the least theta with sqrt(N) mean(c - theta o) / sd(c - theta o) at or below q, the lower root of a quadratic (in its
    // stable form), the unseen task a frequency weight in every sum.
    private static double HedgedLower(IReadOnlyList<PackTrialTask> tasks, int cap, double q, double w)
    {
        if (tasks.Count == 0 || tasks.All(t => t.Offered == 0)) return 0;
        var n = tasks.Count + w;
        double so = w * cap, sc = 0;
        foreach (var t in tasks) { so += t.Offered; sc += t.Correct; }
        var (ob, cb) = (so / n, sc / n);
        var r = cb / ob;
        double soo = w * (cap - ob) * (cap - ob), scc = w * cb * cb, sco = w * (cap - ob) * (0 - cb);
        foreach (var t in tasks)
        {
            var (o, c) = ((double)t.Offered, (double)t.Correct);
            soo += (o - ob) * (o - ob); scc += (c - cb) * (c - cb); sco += (o - ob) * (c - cb);
        }
        (soo, scc, sco) = (soo / (n - 1), scc / (n - 1), sco / (n - 1));
        var a = n * ob * ob - q * q * soo;
        var b = -2 * n * cb * ob + 2 * q * q * sco;
        var c0 = n * cb * cb - q * q * scc;
        if (a <= 0) return 0; // the offered counts vary too much to bound the ratio away from 0
        var discriminant = b * b - 4 * a * c0;
        if (discriminant < 0) return Math.Clamp(r, 0, 1);
        var root = Math.Sqrt(discriminant);
        var lower = -b + root > 0 ? 2 * c0 / (-b + root) : (-b - root) / (2 * a);
        return Math.Clamp(Math.Min(lower, r), 0, 1);
    }

    // An arm's tasks for a bound: its per-task results. Without them nothing is bounded (review 6: items taken as independent
    // passed all-or-none tasks up to a quarter of the time).
    private static IReadOnlyList<PackTrialTask> TasksOf(PackTrialMeasurement arm) =>
        arm.PerTask ?? throw new ArgumentException("A precision bound needs each task's results (PerTask): items are not independent.");

    private static int CapOf(PackTrialMeasurement arm) => Cap(arm.MaximumOffered ?? ModuleTraversal.MaximumOffered);

    // How many consecutive tasks one cluster holds: 1 when the tasks were drawn at least a burst apart (review 6, H4).
    private static int ClusterSize(PackTrialMeasurement arm) => (BurstLength + Math.Clamp(arm.Spacing ?? 1, 1, BurstLength) - 1) / Math.Clamp(arm.Spacing ?? 1, 1, BurstLength);

    // Runs of m consecutive tasks, in the order drawn, each one task of the bound: its items, or, for hit rates, its tasks and hits.
    private static List<PackTrialTask> Clusters(IReadOnlyList<PackTrialTask> tasks, int m) =>
        [.. tasks.Chunk(m).Select(c => new PackTrialTask(c.Sum(t => t.Offered), c.Sum(t => t.Correct)))];

    private static List<PackTrialTask> HitClusters(IReadOnlyList<PackTrialTask> tasks, int m) =>
        [.. tasks.Chunk(m).Select(c => new PackTrialTask(c.Length, c.Count(t => t.Correct > 0)))];

    // 0 is an arm that offers nothing by design (the baseline none); 1 to 16 what it may offer a task.
    private static int Cap(int maximumOffered) => maximumOffered is >= 0 and <= ModuleTraversal.MaximumOffered
        ? maximumOffered : throw new ArgumentOutOfRangeException(nameof(maximumOffered), $"An arm offers 0 to {ModuleTraversal.MaximumOffered} items a task at most.");

    private static void Within(IReadOnlyList<PackTrialTask> tasks, int cap)
    {
        if (tasks.Any(t => t is null || t.Correct < 0 || t.Correct > t.Offered || t.Offered > cap))
            throw new ArgumentException($"Each task offers at most its arm's cap ({cap}) and is right at most as often as it offered.", nameof(tasks));
    }

    // The mean and standard error of task values with the unseen task's value counted w times beside them (a frequency weight).
    private static (double Mean, double StandardError) Weighted(double[] values, double unseen, double w)
    {
        var n = values.Length + w;
        var mean = (values.Sum() + w * unseen) / n;
        var variance = (values.Sum(v => (v - mean) * (v - mean)) + w * (unseen - mean) * (unseen - mean)) / (n - 1);
        return (mean, Math.Sqrt(variance / n));
    }

    private static (IReadOnlyList<PackTrialTask> First, IReadOnlyList<PackTrialTask> Second) Paired(PackTrialMeasurement first, PackTrialMeasurement second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (first.PerTask is not { } a || second.PerTask is not { } b || a.Count != b.Count || a.Count == 0)
            throw new ArgumentException("A paired bound needs each task's results on both arms (PerTask), on the same tasks.");
        return (a, b);
    }

    private static double Quantile(double confidence) => Confidences.TryGetValue(confidence, out var z)
        ? z : throw new ArgumentOutOfRangeException(nameof(confidence), "A trial's confidence is 0.9, 0.95, 0.975 or 0.99.");

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
    /// digits, <paramref name="previous"/> is not an earlier version measured on as many tasks, or a target under a confidence needs
    /// per-task results an arm does not carry (precision and every difference do: items of one task are not independent).</exception>
    public static PackActivation Decide(ModuleManifest manifest, PackTrialMeasurement with, PackTrialMeasurement without, DateTimeOffset decidedAt,
        string? packDigest = null, PackTrialPrevious? previous = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (packDigest is not null && !PackDigestPattern.IsMatch(packDigest))
            throw new ArgumentException("A pack digest is sha256: and 64 lowercase hex digits.", nameof(packDigest));
        ArgumentNullException.ThrowIfNull(with);
        ArgumentNullException.ThrowIfNull(without);
        // Review 1, M4: two arms of one trial ran the same tasks; each arm is consistent in itself, as a certificate requires of
        // the same numbers (review 4: inconsistent numbers gave a wrong bound, which was then signed).
        if (without.Tasks != with.Tasks) throw new ArgumentException("Both arms run the same tasks.", nameof(without));
        if (Inconsistency(with) is { } withProblem) throw new ArgumentException(withProblem, nameof(with));
        if (Inconsistency(without) is { } withoutProblem) throw new ArgumentException(withoutProblem, nameof(without));
        if (previous?.With is { } earlier && Inconsistency(earlier) is { } earlierProblem)
            throw new ArgumentException("The previous version's measurement: " + earlierProblem, nameof(previous));
        // The previous version is earlier, or the same version with other content (a replacement is an upgrade too).
        if (previous is not null && (!ModuleVersion.TryParse(previous.Version, out var before) || before.CompareTo(manifest.Version) > 0
                || before.CompareTo(manifest.Version) == 0 && previous.ManifestDigest == DigestOf(manifest) && previous.PackDigest == packDigest
                || previous.With.Tasks != with.Tasks))
            throw new ArgumentException("The previous version is an earlier version, or other content under this one, measured on the same tasks.", nameof(previous));
        var trial = manifest.Trial ?? throw new ArgumentException($"Module '{manifest.Id}' declares no trial.", nameof(manifest));
        var t = trial.Targets;
        var targets = new List<(string Target, double Required)>();
        if (t.HitRate is { } hit) targets.Add(("hitRate", hit));
        if (t.Precision is { } precision) targets.Add(("precision", precision));
        if (t.Lift is { } lift) targets.Add(("lift", lift));
        if (t.PrecisionLift is { } precisionLift) targets.Add(("precisionLift", precisionLift));
        if (t.AddedP95Ms is { } added) targets.Add(("addedP95Ms", added));
        // An upgrade does not do worse than the version it replaces, on the same tasks, beyond the declared tolerance.
        if (trial.Upgrade is { NoRegression: true } upgrade && previous is not null)
        {
            var floor = upgrade.Tolerance > 0 ? -upgrade.Tolerance : 0;
            targets.Add(("noRegression.hitRate", floor));
            targets.Add(("noRegression.precision", floor));
        }
        var checks = targets.Select(x => Check(x.Target, x.Required, trial.Confidence, with, without, previous?.With)).ToList();
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
            Previous = previous, Upgrade = trial.Upgrade, Confidence = trial.Confidence, Statistic = Statistic,
        };
    }

    // One target checked from the evidence, the one computation a decision and every later check of it share (review 5, root
    // RB): a rate or a difference meets its lower bound when the trial asks for confidence, its estimate otherwise (review 1,
    // M4: a lift on a few tasks cannot pass by luck either); latency is a budget the host sets, compared as measured. With a
    // tolerance above 0, no regression is a non-inferiority test (the lower bound clears -tolerance); at tolerance 0 no
    // measurement could show it, so the estimate must not drop (review 2), which a manifest may ask only without a confidence
    // (review 6: with one, it read as a bound it was not).
    private static PackTrialCheck Check(string target, double required, double? confidence, PackTrialMeasurement with,
        PackTrialMeasurement? without, PackTrialMeasurement? previous)
    {
        if (target == "addedP95Ms")
        {
            var added = with.P95Ms - Arm(without, target).P95Ms;
            return new(target, required, Math.Round(added, 1), added <= required);
        }
        double estimate;
        Func<double, double> bound;
        var slack = 1e-9;
        switch (target)
        {
            case "hitRate":
                (estimate, bound, slack) = (with.HitRate, level => HitRateLower(with, level), 0);
                break;
            case "precision":
                (estimate, bound, slack) = (with.Precision, level => PrecisionLower(with, level), 0);
                break;
            case "lift" or "noRegression.hitRate":
            {
                var other = Arm(target == "lift" ? without : previous, target);
                (estimate, bound) = (with.HitRate - other.HitRate, level => HitDifferenceLower(with, other, level));
                break;
            }
            case "precisionLift" or "noRegression.precision":
            {
                var other = Arm(target == "precisionLift" ? without : previous, target);
                (estimate, bound) = (with.Precision - other.Precision, level => PrecisionDifferenceLower(with, other, level));
                break;
            }
            default:
                throw new ArgumentException($"{target} is not a target this gate checks.");
        }
        if (confidence is not { } level || target.StartsWith("noRegression.", StringComparison.Ordinal) && required >= 0)
            return new(target, required, Math.Round(estimate, 3), estimate >= required - slack);
        var lower = bound(level);
        return new(target, required, Math.Round(lower, 3), lower >= required - slack, Math.Round(estimate, 3));
    }

    private static PackTrialMeasurement Arm(PackTrialMeasurement? arm, string target) =>
        arm ?? throw new ArgumentException($"{target} needs an arm the record does not carry.");

    // Why an arm's numbers cannot be one trial's (null when they can): counts within each other, and per-task results that are
    // one per task, within the arm's cap, summing to its totals, with a correct item on exactly its hits (review 5, L9).
    private static string? Inconsistency(PackTrialMeasurement arm)
    {
        if (arm.MaximumOffered is { } cap && cap is < 0 or > ModuleTraversal.MaximumOffered)
            return $"An arm's MaximumOffered is 0 (it offers nothing by design) to {ModuleTraversal.MaximumOffered}.";
        if (arm.Spacing is { } spacing && spacing is < 1 or > MaximumSpacing)
            return $"An arm's Spacing is 1 to {MaximumSpacing}: every how many eligible commits a task was drawn.";
        var most = arm.MaximumOffered ?? ModuleTraversal.MaximumOffered;
        // A hit is a task with a right item, and a task offers at most the cap: hits are between the right items over the cap
        // and the right items, never above the tasks (review 5).
        if (arm.Tasks < 0 || arm.Hits < 0 || arm.Hits > arm.Tasks || arm.Offered < 0 || arm.Correct < 0 || arm.Correct > arm.Offered
            || arm.Hits > arm.Correct || (long)arm.Hits * most < arm.Correct || arm.Offered > (long)arm.Tasks * most)
            return "An arm hits at most its tasks, a hit is a task with a right item, a task offers at most the arm's cap, and an arm is right at most as often as it offered.";
        if (arm.PerTask is not { } tasks) return null;
        return tasks.Count != arm.Tasks || tasks.Any(x => x is null || x.Correct < 0 || x.Correct > x.Offered || x.Offered > most)
            || tasks.Sum(x => x.Offered) != arm.Offered || tasks.Sum(x => x.Correct) != arm.Correct || tasks.Count(x => x.Correct > 0) != arm.Hits
            ? "PerTask holds one entry per task, 0 <= correct <= offered <= MaximumOffered, summing to Offered and Correct, with a correct item on exactly Hits tasks."
            : null;
    }

    private static readonly ConditionalWeakTable<PackActivation, StrongBox<string?>> Rechecked = new();

    /// <summary>
    /// Why a passing decision does not hold under this library's statistic, or null when it does: its verdict is derived again
    /// from the evidence the record carries, every check at its recorded target and the confidence it declared (review 5, M7:
    /// a record from an earlier statistic waited for a new trial and dropped out of the upgrade chain meanwhile). A verdict
    /// can only be taken back this way, never given: a failed decision stays failed.
    /// </summary>
    public static string? Unmet(PackActivation decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return Rechecked.GetValue(decision, d => new(Why(d))).Value;

        static string? Why(PackActivation decision)
        {
            if (decision.With is not { } with) return "its record carries no measurement";
            if (with.Tasks < ModuleTrial.MinimumTasks) return $"only {with.Tasks} tasks; a trial needs {ModuleTrial.MinimumTasks}";
            IReadOnlyList<PackTrialCheck> again;
            try { again = Recheck(decision); }
            catch (ArgumentException e) { return $"its checks cannot be derived from what the record carries: {e.Message}"; }
            return again.FirstOrDefault(c => !c.Met) is { } failed
                ? $"{failed.Target} {failed.Observed.ToString(CultureInfo.InvariantCulture)} {(failed.Target == "addedP95Ms" ? "above" : "below")} {failed.Required.ToString(CultureInfo.InvariantCulture)}"
                : null;
        }
    }

    /// <summary>
    /// A decision's checks derived again from the evidence it records, under this library's <see cref="Statistic"/>: each
    /// recorded target at its recorded value, at the confidence the decision declared. The one computation the gate, an
    /// upgrade's comparison, a certificate and an admission share.
    /// </summary>
    /// <exception cref="ArgumentException">The record does not carry the evidence a check needs (a measurement, an arm, per-task results).</exception>
    public static IReadOnlyList<PackTrialCheck> Recheck(PackActivation decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var with = decision.With ?? throw new ArgumentException("The record carries no measurement.", nameof(decision));
        return [.. decision.Checks.Select(c => Check(c.Target, c.Required, decision.Confidence, with, decision.Without, decision.Previous?.With))];
    }

    /// <summary>
    /// The registrations that may compose, and the ones left off: a module that declares a trial needs a passing decision for
    /// exactly its manifest and, when it came from a pack, exactly that pack; a decision the host made from a certificate counts
    /// only in a namespace where it trusts certificates, and elsewhere its latest own trial decides; in a namespace where the
    /// host made trials mandatory, a module that declares none is left off; a module that needs one left off is left off too.
    /// </summary>
    internal static (IReadOnlyList<ModuleRegistration> On, IReadOnlyList<SwitchedOffModule> Off) Apply(IReadOnlyList<ModuleRegistration> registrations,
        IPackActivations? activations, IReadOnlyList<string>? mandatory = null, IReadOnlyList<string>? certificateTrust = null, PackTrialFloor? floor = null)
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
            var reason = floor is not null && Below(manifest.Trial!, floor) is { } below ? $"its trial claims less than this host requires: {below}"
                : decision is not null ? (!decision.Active ? "its trial failed: " + decision.Reason
                    : Unmet(decision) is { } unmet ? $"its trial's evidence does not meet its targets under {Statistic}: {unmet}; trial it again"
                    : Upgrade(activations, manifest, decision, certificatesCount))
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

    // What a trial claims short of the host's floor, or null: the confidence, the tasks, and each target the floor names.
    internal static string? Below(ModuleTrial trial, PackTrialFloor floor)
    {
        var gaps = new List<string>();
        static string S(double v) => v.ToString(CultureInfo.InvariantCulture);
        if (floor.Confidence is { } least)
        {
            if (trial.Confidence is not { } declared) gaps.Add($"no confidence, {S(least)} required");
            else if (declared < least) gaps.Add($"confidence {S(declared)} below {S(least)}");
        }
        if (trial.Tasks < floor.Tasks) gaps.Add($"{trial.Tasks} tasks, {floor.Tasks} required");
        if (floor.Targets is { } f)
        {
            var t = trial.Targets;
            void AtLeast(string name, double? required, double? declared)
            {
                if (required is not { } r) return;
                if (declared is not { } d) gaps.Add($"no {name} target, {S(r)} required");
                else if (d < r) gaps.Add($"{name} {S(d)} below {S(r)}");
            }
            AtLeast("hitRate", f.HitRate, t.HitRate);
            AtLeast("precision", f.Precision, t.Precision);
            AtLeast("lift", f.Lift, t.Lift);
            AtLeast("precisionLift", f.PrecisionLift, t.PrecisionLift);
            if (f.AddedP95Ms is { } budget)
            {
                if (t.AddedP95Ms is not { } added) gaps.Add($"no addedP95Ms target, {S(budget)} required");
                else if (added > budget) gaps.Add($"addedP95Ms {S(added)} above {S(budget)}");
            }
        }
        return gaps.Count == 0 ? null : string.Join("; ", gaps);
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
        Settle(activations, decision, Rule(decision.Upgrade ?? manifest.Trial?.Upgrade, decision.Confidence),
            new Dictionary<PackActivation, (string?, HeldRule)>(ReferenceEqualityComparer.Instance), certificatesCount).Refusal;

    // One no-regression rule a version declared: its tolerance, and the confidence its bound is taken at (none: the estimate).
    private sealed record NoRegressionRule(double Tolerance, double? Confidence);

    // Every rule an upgrade is held to, each checked as it was declared (review 5, M6: the confidence travels with its rule;
    // review 6: two rules are not ordered, so the stricter of two was a blend that could be looser than either, a tolerance of
    // 0 checked on the estimate in place of a predecessor's bound at 0.1). Empty: no regression was never asked for.
    private sealed record HeldRule(IReadOnlyList<NoRegressionRule> Rules);

    private static HeldRule Rule(ModuleTrialUpgrade? upgrade, double? confidence) =>
        new(upgrade is { NoRegression: true } u ? [new NoRegressionRule(u.Tolerance, confidence)] : []);

    // Why a passing decision may not go on as an upgrade (or null), and the rule it is held to: the stricter of its own and
    // the one its predecessor was held to, so a rule carries down the whole chain, not one version (third review). The version
    // it replaces is the one that passed here before it: of every content's current decision (a later failing one supersedes
    // a pass), a passing one that still holds under this library's statistic and that the gate itself allows, at an earlier
    // version, or at this version with other content decided earlier (on the same instant, the lower digest first); the
    // highest, the latest.
    private static (string? Refusal, HeldRule Rule) Settle(IPackActivations activations, PackActivation decision, HeldRule rule,
        Dictionary<PackActivation, (string?, HeldRule)> known, bool certificatesCount)
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
            .Where(a => a.Decision.Active && Unmet(a.Decision) is null && a.Version is not null && Content(a.Decision) != content
                && (a.Version.CompareTo(version) < 0 || a.Version.CompareTo(version) == 0
                    && (a.Decision.DecidedAt < decision.DecidedAt || a.Decision.DecidedAt == decision.DecidedAt && string.CompareOrdinal(Content(a.Decision), content) < 0)))
            .OrderByDescending(a => a.Version).ThenByDescending(a => a.Decision.DecidedAt).ThenByDescending(a => Content(a.Decision), StringComparer.Ordinal)
            .Select(a => (a.Decision, Settled: Settle(activations, a.Decision, Rule(a.Decision.Upgrade, a.Decision.Confidence), known, certificatesCount)))
            .FirstOrDefault(a => a.Settled.Refusal is null);
        var held = active.Decision is null ? rule : Stricter(rule, active.Settled.Rule);
        var result = (active.Decision is null ? null : Against(decision, held, active.Decision), held);
        known[decision] = result;
        return result;
    }

    private static string Content(PackActivation decision) => (decision.PackDigest ?? "") + "|" + decision.ManifestDigest;

    // Both rules: every one either carries, each as declared.
    private static HeldRule Stricter(HeldRule a, HeldRule b) => new([.. a.Rules.Concat(b.Rules).Distinct()]);

    // An upgrade against the version it replaces, held to the rule the chain carries, from the measurements the record
    // carries, whatever its own checks said: the same test as a decision's own (review 2), bounds at the held confidence when
    // the tolerance is above 0, under this library's statistic (review 5, M7: no longer refused for a record from another).
    private static string? Against(PackActivation decision, HeldRule held, PackActivation active)
    {
        if (held.Rules.Count == 0) return null;
        if (decision.Previous is not { } previous)
            return $"it upgrades {active.Version}, which passed here; its trial must compare against that version";
        if (previous.Version != active.Version || previous.ManifestDigest != active.ManifestDigest || previous.PackDigest != active.PackDigest)
            return $"its trial compared against {previous.Version}, but {active.Version} ({Short(active.PackDigest ?? active.ManifestDigest)}) is the version that passed here";
        if (decision.With is not { } with) return "its record carries no measurement to compare";
        foreach (var rule in held.Rules)
        {
            var floor = rule.Tolerance > 0 ? -rule.Tolerance : 0;
            PackTrialCheck hit, precision;
            try
            {
                hit = Check("noRegression.hitRate", floor, rule.Confidence, with, null, previous.With);
                precision = Check("noRegression.precision", floor, rule.Confidence, with, null, previous.With);
            }
            catch (ArgumentException e) { return $"it cannot be compared with {active.Version} from what its record carries: {e.Message}"; }
            if (!hit.Met || !precision.Met)
                return $"it does worse than {active.Version} beyond the tolerance {rule.Tolerance.ToString(CultureInfo.InvariantCulture)}"
                    + (rule.Confidence is { } c ? $" at {c.ToString(CultureInfo.InvariantCulture)}" : "")
                    + $" (hit rate {hit.Observed.ToString(CultureInfo.InvariantCulture)}, precision {precision.Observed.ToString(CultureInfo.InvariantCulture)})";
        }
        return null;
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

    // A decision with every task's results for both arms and a predecessor, 200 tasks, is a few tens of KiB.
    private const int MaximumRecordBytes = 1 << 20;

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
                // Bounded and never through a link, as every file a host reads (review 6, M2); one that is not is not a record.
                byte[] bytes;
                try { bytes = BoundedFile.Read(file, MaximumRecordBytes, "An activation record"); }
                catch (Exception e) when (e is InvalidDataException or IOException) { ignored.Add(Path.GetFileName(file)); continue; }
                var activation = Open(bytes, key);
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
            using var existing = JsonDocument.Parse(BoundedFile.Read(file, MaximumRecordBytes, "An activation record"));
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
