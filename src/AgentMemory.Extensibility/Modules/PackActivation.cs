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
    /// For a retrieval trial (feature 12), the abstention tasks run (their expected result is nothing), apart from <see cref="Tasks"/>,
    /// and how many of them the arm offered nothing on. Null for a trial without abstention tasks.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Abstentions { get; init; }
    /// <summary>The abstention tasks the arm offered nothing on.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Quiet { get; init; }
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
public sealed record PackTrialTask(int Offered, int Correct)
{
    /// <summary>
    /// The UTF-8 bytes of what the arm offered this task, framed as the model reads it (feature 05, <see cref="PackTrialRender"/>);
    /// absent from records written before it, and from the record when not measured.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public long? OfferedBytes { get; init; }
}

/// <summary>
/// One part of a pack, measured on its own (feature 11, R3): the first relation of the offers' reason paths, or <c>(anchor)</c>;
/// its arm, the trial's arm without the pack, the corrected confidence it was decided at, and whether it passed alone.
/// </summary>
[Experimental("AMEXT001")]
public sealed record PackTrialPart
{
    /// <summary><c>NAME:out</c>, <c>NAME:in</c> or <c>(anchor)</c>.</summary>
    public required string Part { get; init; }
    /// <summary>What the part offered, task by task.</summary>
    public required PackTrialMeasurement With { get; init; }
    /// <summary>The trial's arm without the pack.</summary>
    public required PackTrialMeasurement Without { get; init; }
    /// <summary>The confidence the part was decided at: the trial's, corrected for the number of parts (Bonferroni).</summary>
    public double? Confidence { get; init; }
    /// <summary>Whether it met every target alone.</summary>
    public required bool Admitted { get; init; }
}

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
    /// <summary>What the host's safety probes showed, when the trial declares a safety target: kept so a recheck decides alike.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PackTrialSafetyRun? Safety { get; init; }
    /// <summary>What the pack adds to every turn, in bytes, when the trial declares a cost target: kept so a recheck decides alike.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public long? FixedBytes { get; init; }

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
    /// <summary>
    /// For a decision that admits the pack in part (feature 11, R3), every part measured, each with its own evidence; the gate
    /// admits the parts that pass alone, rechecked as the whole is. Null when the decision is for the whole pack.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PackTrialPart>? Parts { get; init; }
    /// <summary>
    /// For a retrieval trial (feature 12), the host's task file: <c>sha256:</c> over its exact bytes, so the decision names the
    /// tasks it ran on without revealing them. Null for a trial drawn from code history.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TaskFile { get; init; }
    /// <summary>For a retrieval trial, each ability's arm with the pack, reported beside the decision and not gating it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, PackTrialMeasurement>? Abilities { get; init; }
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
public static partial class PackTrialGate
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
    public const string Statistic = "agentmemory.trial.hedged/4";

    /// <summary>
    /// The most eligible commits one burst of related changes is taken to span (review 6, H4), the one assumption about the
    /// order of a host's history: a trial whose tasks were drawn at least this far apart counts each as independent; closer,
    /// each run of ⌈8 / spacing⌉ consecutive tasks is one cluster, and neighbouring clusters count as correlated, since a burst
    /// can straddle two (review 8, G1). Where the assumption holds, every clustered bound passes a target at the truth at most
    /// its nominal rate at the regime's worst case, checked per burst layout, not averaged over layouts: bursts of exactly 8 at
    /// every offset, intraclass correlation 0.6 and 1, spacings 1 to 7, 40 to 200 tasks (eng/trial-statistics/bursts.py: worst
    /// 3.8% at 95%, 0.65% at 99%; counted as independent clusters, 9.4% and 2.9%). Where it does not, the
    /// rate rises: bursts of geometric length averaging 5 (longer than 8 about 17% of the time), at spacing 8, where tasks are
    /// first taken as independent, pass 5.3% at 95% and 1.5% at 99%; averaging 8, 6.9% and 2.0% (eng/trial-statistics/serial.py,
    /// hedged/4). The neighbouring-cluster term costs two to three points of power on short histories (at 200 eligible commits for
    /// 40 tasks, a pack 0.15 above its target passes 36% of the time at 95%, against 39% counted as independent clusters).
    /// </summary>
    public const int BurstLength = 8;

    /// <summary>The most bytes one task of an arm may record offering (feature 05), as a certificate bounds them.</summary>
    public const long MaximumTaskBytes = 1_000_000_000;

    /// <summary>
    /// The history a trial's tasks come from: the most recent 2,000 eligible commits, which the runner reads and no further.
    /// Every arm's tasks lie inside it, so the spacing times one fewer than the tasks stays below it (review 7, G5: 40 tasks at a
    /// spacing of 2,000, 80,000 commits, were accepted and bounded as if far apart).
    /// </summary>
    public const int HistoryWindow = 2000;

    /// <summary>The largest spacing a measurement records: one task in the whole <see cref="HistoryWindow"/>.</summary>
    public const int MaximumSpacing = HistoryWindow;

    /// <summary>
    /// How many times the unseen worst-case task counts, by confidence: the least weight, in quarters, at which a paired hit
    /// difference equal to its target passes at most the nominal rate for every n from 10 to 200 and every pair of rates, by
    /// exact trinomial enumeration (eng/trial-statistics/hitdiff_exact.py: 9.4%, 4.6%, 2.1% and 0.85%); the one-arm and paired
    /// precision bounds hold with it too (the harness).
    /// </summary>
    internal static double HedgeWeight(double confidence) =>
        // The registry's quarters (review F02, M7); a confidence outside the closed set is no confidence of the statistic.
        Confidences.ContainsKey(confidence) && PackRegistry.Quantiles.TryGetValue((int)Math.Round(confidence * 1000), out var q)
            ? q.HedgeQuarters / 4.0 : throw new ArgumentOutOfRangeException(nameof(confidence), "A trial's confidence is 0.9, 0.95, 0.975 or 0.99.");

    /// <summary>The one-sided normal quantiles the trial's confidence may name.</summary>
    // The integers of a trial (feature 02) as the fractions the floating-point statistic still computes with; step 4 of
    // feature 02 decides exactly and these go.
    private static double? F(long? micro) => micro is { } m ? PackNumbers.Fraction(m) : null;
    private static double? Level(int? permille) => permille is { } p ? p / 1000.0 : null;

    internal static readonly IReadOnlyDictionary<double, double> Confidences = PackRegistry.Quantiles.ToDictionary(
        q => q.Key / 1000.0, q => double.Parse(q.Value.Z, CultureInfo.InvariantCulture));

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
        return ClusteredLower(HitClusters(TasksOf(arm), m), m, confidence);
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
        return ClusteredLower(Clusters(tasks, m), m * cap, confidence);
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
            return PairedDifference(HitClusters(a, m), HitClusters(b, m), m, m, confidence, lag1: true);
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
            ? PairedDifference(Clusters(a, m), Clusters(b, m), m * cap1, m * cap2, confidence, lag1: true)
            : PairedDifference(a, b, cap1, cap2, confidence);
    }

    // The paired difference of two pooled ratios over the same units (tasks, or clusters of them), each unit at most its arm's
    // cap: the unseen unit, the first arm offering its cap all wrong and the second its cap all right, counted HedgeWeight times.
    // Clusters in drawn order are 1-dependent, so their variance takes the lag-1 term (review 8, G1).
    private static double PairedDifference(IReadOnlyList<PackTrialTask> a, IReadOnlyList<PackTrialTask> b, int cap1, int cap2, double confidence,
        bool lag1 = false)
    {
        var q = StudentT(Quantile(confidence), a.Count - 1);
        var w = HedgeWeight(confidence);
        double o1 = w * cap1, c1 = 0, o2 = w * cap2, c2 = w * cap2;
        for (var i = 0; i < a.Count; i++) { o1 += a[i].Offered; c1 += a[i].Correct; o2 += b[i].Offered; c2 += b[i].Correct; }
        var n = a.Count + w;
        var (r1, r2, m1, m2) = (c1 / o1, c2 / o2, o1 / n, o2 / n);
        var f = new double[a.Count];
        for (var i = 0; i < a.Count; i++) f[i] = (a[i].Correct - r1 * a[i].Offered) / m1 - (b[i].Correct - r2 * b[i].Offered) / m2;
        var unseen = (0 - r1 * cap1) / m1 - (cap2 - r2 * cap2) / m2;
        var (_, se) = lag1 ? WeightedLag1(f, unseen, w) : Weighted(f, unseen, w);
        return Math.Clamp(r1 - r2 - q * se, -1, 1);
    }

    /// <summary>
    /// The lower confidence bound of the pooled precision of clusters of consecutive tasks, in the order drawn (review 8, G1): the
    /// hedged ratio (the unseen cluster offering <paramref name="cap"/> items, none right, counted <see cref="HedgeWeight"/>
    /// times), linearized at its estimate, with the clusters' lag-1 autocovariance in its variance, at Student's t with one degree
    /// of freedom fewer than the clusters. A burst of at most <see cref="BurstLength"/> commits can straddle two clusters, so
    /// neighbouring clusters are correlated: counted as independent, a hit-rate target at the truth passed 9.4% of the time at
    /// 95% for bursts at an offset to the clusters.
    /// </summary>
    private static double ClusteredLower(IReadOnlyList<PackTrialTask> units, int cap, double confidence)
    {
        double offered = 0, correct = 0;
        foreach (var unit in units) { offered += unit.Offered; correct += unit.Correct; }
        if (units.Count == 0 || offered == 0) return 0;
        var w = HedgeWeight(confidence);
        var r = correct / (offered + w * cap);
        var ob = (offered + w * cap) / (units.Count + w);
        var z = new double[units.Count];
        for (var i = 0; i < units.Count; i++) z[i] = units[i].Correct - r * units[i].Offered;
        var (_, se) = WeightedLag1(z, -r * cap, w);
        return Math.Clamp(r - StudentT(Quantile(confidence), units.Count - 1) * se / ob, 0, 1);
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
        if (Level(trial.ConfidencePermille) is not { } level || !Confidences.ContainsKey(level)) return [];
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
        if (F(t.HitRateMicro) is { } hit) checks.Add(("hitRate", hit, n => HitRateLower(Arm(n, cap, cap, cap), level)));
        if (F(t.PrecisionMicro) is { } precision) checks.Add(("precision", precision, n => PrecisionLower(Arm(n, cap, cap, cap), level)));
        PackTrialMeasurement Baseline(int n) => trial.Baseline == "host" ? Arm(n, 3, 0, 3) : Arm(n, 0, 0, 0);
        if (F(t.LiftMicro) is { } lift) checks.Add(("lift", lift, n => HitDifferenceLower(Arm(n, cap, cap, cap), Baseline(n), level)));
        if (F(t.PrecisionLiftMicro) is { } precisionLift) checks.Add(("precisionLift", precisionLift, n => PrecisionDifferenceLower(Arm(n, cap, cap, cap), Baseline(n), level)));
        var notes = new List<string>();
        string Needed(Func<int, double> bound, double required) =>
            Enumerable.Range(trial.Tasks + 1, Math.Max(0, ModuleTrial.MaximumTasks - trial.Tasks)).FirstOrDefault(n => bound(n) >= required - 1e-9) is var n and > 0
                ? $"{n} tasks would" : $"no trial of up to {ModuleTrial.MaximumTasks} tasks would";
        foreach (var (target, required, best) in checks)
            if (best(trial.Tasks) is var bound && bound < required - 1e-9)
                notes.Add($"{target} {required.ToString(CultureInfo.InvariantCulture)} cannot be met on {trial.Tasks} tasks at {level.ToString(CultureInfo.InvariantCulture)}: "
                    + $"even the best evidence bounds it at {Math.Round(bound, 3).ToString(CultureInfo.InvariantCulture)}; {Needed(best, required)}.");
        if (trial.Upgrade is { NoRegression: true, ToleranceMicro: > 0 } upgrade)
        {
            // Two versions that agree on every task: only the tasks the trial did not see separate them. Both checks, hit rate
            // and precision, at their best: every task offering the cap (review 6, M7: a pack that offers fewer items than its
            // cap needs more, since the unseen task offers the cap; on the code map's own forty tasks, -0.104 at a cap of 3).
            double Same(int n) => Math.Min(HitDifferenceLower(Arm(n, cap, 1, cap), Arm(n, cap, 1, cap), level),
                PrecisionDifferenceLower(Arm(n, cap, 1, cap), Arm(n, cap, 1, cap), level));
            if (Same(trial.Tasks) is var same && same < -PackNumbers.Fraction(upgrade.ToleranceMicro) - 1e-9)
                notes.Add($"An upgrade exactly as good as this version cannot clear the tolerance {PackNumbers.Fraction(upgrade.ToleranceMicro).ToString(CultureInfo.InvariantCulture)} on {trial.Tasks} tasks: "
                    + $"agreeing on every task bounds the drop at {Math.Round(same, 3).ToString(CultureInfo.InvariantCulture)} at best; {Needed(Same, -PackNumbers.Fraction(upgrade.ToleranceMicro))}, or a tolerance of {Math.Ceiling(-same * 100) / 100:0.00}. "
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
    // As Weighted, with the values' lag-1 autocovariance in their order added to the variance, never lowering it.
    private static (double Mean, double StandardError) WeightedLag1(double[] values, double unseen, double w)
    {
        var n = values.Length + w;
        var mean = (values.Sum() + w * unseen) / n;
        var g0 = (values.Sum(v => (v - mean) * (v - mean)) + w * (unseen - mean) * (unseen - mean)) / (n - 1);
        double g1 = 0;
        for (var i = 0; i + 1 < values.Length; i++) g1 += (values[i] - mean) * (values[i + 1] - mean);
        g1 /= n - 1;
        return (mean, Math.Sqrt(Math.Max(g0, g0 + 2 * g1) / n));
    }

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
    // Student's t quantiles at the four confidences, for 1 to 60 degrees of freedom: the registry's decimals (review F02, M7).
    private static readonly IReadOnlyDictionary<double, double[]> StudentQuantiles = PackRegistry.Quantiles.Values.ToDictionary(
        q => double.Parse(q.Z, CultureInfo.InvariantCulture), q => q.T.Select(t => double.Parse(t, CultureInfo.InvariantCulture)).ToArray());

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

    /// <summary>The part an anchor's own record belongs to (feature 11).</summary>
    public const string AnchorPart = "(anchor)";

    /// <summary>
    /// A decision that may admit a pack in part (feature 11, R3): the whole pack when it passes; otherwise each part decided alone
    /// on its own arm, at the trial's confidence corrected for the number of parts (Bonferroni: the next confidence the exact
    /// tables hold at or above 1 - (1 - c) / parts), and admitted where it passes. With no such confidence, no part is admitted.
    /// </summary>
    public static PackActivation DecideParts(ModuleManifest manifest, PackTrialMeasurement with, PackTrialMeasurement without,
        IReadOnlyDictionary<string, PackTrialMeasurement> parts, DateTimeOffset decidedAt, string? packDigest = null)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var whole = Decide(manifest, with, without, decidedAt, packDigest);
        if (whole.Active || parts.Count == 0) return whole;
        var (possible, permille) = Corrected(manifest.Trial!.ConfidencePermille, parts.Count);
        var corrected = manifest with { Trial = manifest.Trial with { ConfidencePermille = permille } };
        var decided = parts.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new PackTrialPart
        {
            Part = p.Key, With = p.Value, Without = without, Confidence = Level(permille),
            Admitted = possible && Decide(corrected, p.Value, without, decidedAt).Active,
        }).ToList();
        // Reported, never admitted (review 11-13, M3 and M4: the union that would go live was never measured, and testing the
        // whole at c and then the parts made the error rate about twice the claimed one). A sound design picks parts on one half of
        // the tasks and confirms their union on the other.
        var passing = decided.Where(p => p.Admitted).Select(p => p.Part).ToList();
        return whole with
        {
            Parts = decided,
            Reason = whole.Reason + (passing.Count != 0
                ? $"; parts that pass alone, reported and not admitted: {string.Join(", ", passing)} (confidence {Level(permille)?.ToString(CultureInfo.InvariantCulture) ?? "none, estimates"} for {parts.Count} parts)"
                : possible ? "; no part passes alone at the corrected confidence" : $"; {parts.Count} parts need a confidence the exact tables do not hold"),
        };
    }

    // The trial's confidence corrected for this many parts (Bonferroni on alpha): the least the tables hold at or above
    // 1 - (1 - c) / parts. Without a confidence the targets are estimates, which no correction changes.
    internal static (bool Possible, int? Permille) Corrected(int? permille, int parts)
    {
        if (permille is not { } p || parts <= 1) return (true, permille);
        var alpha = (1000 - p) / (double)parts;
        return PackRegistry.Quantiles.Keys.Where(q => 1000 - q <= alpha + 1e-9).Order().FirstOrDefault() is var least && least != 0 ? (true, least) : (false, null);
    }

    /// <summary>The parts of a partial decision that still pass alone, rechecked from their own evidence; null for a whole decision.</summary>
    internal static IReadOnlySet<string>? HeldParts(PackActivation decision) =>
        decision.Parts is not { } parts ? null
            : parts.Where(p => p.Admitted && Unmet(decision with { With = p.With, Without = p.Without, Confidence = p.Confidence, Parts = null, Previous = null }) is null)
                .Select(p => p.Part).ToHashSet(StringComparer.Ordinal);

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
    /// <param name="safety">What the host's own safety probes showed (<see cref="IPackSafetyProbes"/>), when the trial declares a
    /// safety target; without it that target is not met.</param>
    /// <exception cref="ArgumentException">The manifest declares no trial, <paramref name="packDigest"/> is not <c>sha256:</c> and 64 hex
    /// digits, <paramref name="previous"/> is not an earlier version measured on as many tasks, or a target under a confidence needs
    /// per-task results an arm does not carry (precision and every difference do: items of one task are not independent).</exception>
    public static PackActivation Decide(ModuleManifest manifest, PackTrialMeasurement with, PackTrialMeasurement without, DateTimeOffset decidedAt,
        string? packDigest = null, PackTrialPrevious? previous = null, PackTrialSafetyRun? safety = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (packDigest is not null && !PackDigestPattern.IsMatch(packDigest))
            throw new ArgumentException("A pack digest is sha256: and 64 lowercase hex digits.", nameof(packDigest));
        ArgumentNullException.ThrowIfNull(with);
        ArgumentNullException.ThrowIfNull(without);
        // The caps the bounds rest on come from the declarations: where a measurement leaves one out it is the declared one,
        // where it records one it must be the declared one (review 7, G4).
        (with, without) = Declared(manifest, with, without);
        if (CapProblem(manifest, with, without) is { } capProblem) throw new ArgumentException(capProblem.Problem, capProblem.Arm);
        // Review 1, M4: two arms of one trial ran the same tasks; each arm is consistent in itself, as a certificate requires of
        // the same numbers (review 4: inconsistent numbers gave a wrong bound, which was then signed). The one intake every
        // later check of this decision passes too (review 7, G13).
        if (Intake(with, without, previous?.With) is { } refused) throw new ArgumentException(refused.Problem, refused.Arm);
        // The previous version is earlier, or the same version with other content (a replacement is an upgrade too).
        if (previous is not null && (!ModuleVersion.TryParse(previous.Version, out var before) || before.CompareTo(manifest.Version) > 0
                || before.CompareTo(manifest.Version) == 0 && previous.ManifestDigest == DigestOf(manifest) && previous.PackDigest == packDigest
                || previous.With.Tasks != with.Tasks))
            throw new ArgumentException("The previous version is an earlier version, or other content under this one, measured on the same tasks.", nameof(previous));
        var trial = manifest.Trial ?? throw new ArgumentException($"Module '{manifest.Id}' declares no trial.", nameof(manifest));
        var t = trial.Targets;
        var targets = t.Checked().Select(c => (Target: c.Name, Required: c.Value)).ToList();
        // An upgrade does not do worse than the version it replaces, on the same tasks, beyond the declared tolerance.
        if (trial.Upgrade is { NoRegression: true } upgrade && previous is not null)
        {
            var floor = PackNumbers.Fraction(upgrade.ToleranceMicro) > 0 ? -PackNumbers.Fraction(upgrade.ToleranceMicro) : 0;
            targets.Add(("noRegression.hitRate", floor));
            targets.Add(("noRegression.precision", floor));
        }
        // The safety target (feature 04): the follow rate the host's own probes showed, at most the declared share.
        if (trial.Safety is { } declared)
        {
            if (safety is not null && (safety.Suite != declared.Probes || safety.Probes < 1 || safety.Followed < 0 || safety.Followed > safety.Probes))
                throw new ArgumentException($"The safety run is of suite '{safety.Suite}' with {safety.Followed} of {safety.Probes} followed; the trial declares '{declared.Probes}'.", nameof(safety));
            targets.Add(("safety", PackNumbers.Fraction(declared.MaxFollowMicro)));
        }
        // The cost targets (feature 05): a saving per task in what the model reads, after the pack's fixed cost.
        long? fixedBytes = null;
        if (trial.Cost is { } cost)
        {
            fixedBytes = PackTrialRender.FixedBytes(manifest);
            if (cost.SavingBytesMedian is { } bytes) targets.Add(("savingBytes", bytes));
            if (cost.SavingRatioMicro is { } ratio) targets.Add(("savingRatio", PackNumbers.Fraction(ratio)));
        }
        var checks = targets.Select(x => Check(x.Target, x.Required, Level(trial.ConfidencePermille), with, without, previous?.With, safety, fixedBytes)).ToList();
        var enough = with.Tasks >= ModuleTrial.MinimumTasks;
        var failed = checks.Where(c => !c.Met).ToList();
        var reason = !enough ? $"only {with.Tasks} tasks in this host's history; a trial needs {ModuleTrial.MinimumTasks}"
            : failed.Count == 0 ? "every target met"
            : string.Join("; ", failed.Select(c => c.Target is "addedP95Ms" or "safety"
                ? $"{c.Target} {c.Observed.ToString(CultureInfo.InvariantCulture)} above {c.Required.ToString(CultureInfo.InvariantCulture)}"
                : $"{c.Target} {c.Observed.ToString(CultureInfo.InvariantCulture)} below {c.Required.ToString(CultureInfo.InvariantCulture)}"));
        return new PackActivation
        {
            ModuleId = manifest.Id, Version = manifest.Version.ToString(), ManifestDigest = DigestOf(manifest), PackDigest = packDigest,
            Active = enough && failed.Count == 0, Reason = reason, DecidedAt = decidedAt, Checks = checks, With = with, Without = without,
            Previous = previous, Upgrade = trial.Upgrade, Confidence = Level(trial.ConfidencePermille), Statistic = Statistic, Safety = trial.Safety is null ? null : safety, FixedBytes = fixedBytes,
        };
    }

    // One target checked from the evidence, the one computation a decision and every later check of it share (review 5, root
    // RB): a rate or a difference meets its lower bound when the trial asks for confidence, its estimate otherwise (review 1,
    // M4: a lift on a few tasks cannot pass by luck either); latency is a budget the host sets, compared as measured. With a
    // tolerance above 0, no regression is a non-inferiority test (the lower bound clears -tolerance); at tolerance 0 no
    // measurement could show it, so the estimate must not drop (review 2), which a manifest may ask only without a confidence
    // (review 6: with one, it read as a bound it was not).
    private static PackTrialCheck Check(string target, double required, double? confidence, PackTrialMeasurement with,
        PackTrialMeasurement? without, PackTrialMeasurement? previous, PackTrialSafetyRun? safety = null, long? fixedBytes = null)
    {
        if (target is "savingBytes" or "savingRatio") return CostCheck(target, required, confidence, with, Arm(without, target), fixedBytes ?? 0);
        if (target == "safety")
        {
            // Met when the follow rate is at most the target: its estimate, or under a confidence its upper bound, which is one
            // less the lower bound of the probes not followed (Clopper-Pearson), decided exactly. No run, no pass.
            if (safety is not { Probes: > 0 } || safety.Followed < 0 || safety.Followed > safety.Probes) return new(target, required, 1, false);
            var most = Q.Of(checked((long)Math.Round(required * PackNumbers.One)), PackNumbers.One);
            var share = Q.Of(safety.Followed, safety.Probes);
            var shown = (double)safety.Followed / safety.Probes;
            if (confidence is not { } c) return new(target, required, Math.Round(shown, 6), share <= most);
            var upper = 1 - RateLower(safety.Probes - safety.Followed, safety.Probes, c);
            return new(target, required, Math.Ceiling(upper * PackNumbers.One) / PackNumbers.One,
                MeetsRate(safety.Probes - safety.Followed, safety.Probes, (int)Math.Round(c * 1000), 1 - most), Math.Round(shown, 6));
        }
        if (target == "noHarm")
        {
            // Met when the share of abstention tasks the pack stayed quiet on is at least the target: its estimate, or under a
            // confidence its exact lower bound (Clopper-Pearson), decided as a rate is. No abstention tasks, no pass.
            if (with.Abstentions is not { } n || n <= 0 || with.Quiet is not { } q || q < 0 || q > n) return new(target, required, 0, false);
            var least = Q.Of(checked((long)Math.Round(required * PackNumbers.One)), PackNumbers.One);
            var shown = (double)q / n;
            if (confidence is not { } c) return new(target, required, Math.Round(shown, 6), Q.Of(q, n) >= least);
            return new(target, required, Math.Floor(RateLower(q, n, c) * PackNumbers.One) / PackNumbers.One,
                MeetsRate(q, n, (int)Math.Round(c * 1000), least), Math.Round(shown, 6));
        }
        if (target == "addedP95Ms")
        {
            // In whole microseconds, as a certificate records latency (review 7, G9: 300.3 - 50.1 is 250.20000000000002 in binary,
            // above a budget of 250.2).
            var added = Micros(with.P95Ms) - Micros(Arm(without, target).P95Ms);
            return new(target, required, Math.Round(added / 1000.0, 1), added <= Micros(required));
        }
        // The verdict is exact (feature 02): the estimate as a ratio of counts, the bound decided by PackTrialExact against the
        // target's own millionths. The floating-point values are what the check shows, never what it decides.
        var t = Q.Of(checked((long)Math.Round(required * PackNumbers.One)), PackNumbers.One);
        double estimate;
        Q exact;
        Func<double, double> bound;
        Func<int, Q, bool> meets;
        switch (target)
        {
            case "hitRate":
                (estimate, exact, bound, meets) = (with.HitRate, RateOf(with), level => HitRateLower(with, level), (p, x) => MeetsHitRate(with, p, x));
                break;
            case "precision":
                (estimate, exact, bound, meets) = (with.Precision, PrecisionOf(with), level => PrecisionLower(with, level), (p, x) => MeetsPrecision(with, p, x));
                break;
            case "lift" or "noRegression.hitRate":
            {
                var other = Arm(target == "lift" ? without : previous, target);
                (estimate, exact, bound, meets) = (with.HitRate - other.HitRate, RateOf(with) - RateOf(other), level => HitDifferenceLower(with, other, level),
                    (p, x) => MeetsHitDifference(with, other, p, x));
                break;
            }
            case "precisionLift" or "noRegression.precision":
            {
                var other = Arm(target == "precisionLift" ? without : previous, target);
                (estimate, exact, bound, meets) = (with.Precision - other.Precision, PrecisionOf(with) - PrecisionOf(other),
                    level => PrecisionDifferenceLower(with, other, level), (p, x) => MeetsPrecisionDifference(with, other, p, x));
                break;
            }
            default:
                throw new ArgumentException($"{target} is not a target this gate checks.");
        }
        // Shown in millionths, as the targets are (review F02, M5): a bound rounded down, so it shows at or above the target
        // exactly when it was met (bar a tie the float bound misses, where the exact verdict stands); an estimate to the nearest.
        if (confidence is not { } level || target.StartsWith("noRegression.", StringComparison.Ordinal) && required >= 0)
            return new(target, required, Math.Round(estimate, 6), exact >= t);
        var lower = bound(level);
        return new(target, required, Math.Floor(lower * PackNumbers.One) / PackNumbers.One, meets((int)Math.Round(level * 1000), t), Math.Round(estimate, 6));
    }

    // A cost target (feature 05, R-TRIAL-056): each task's saving in the target's unit, the baseline's framed bytes less the
    // pack's and its fixed cost (whole bytes, or millionths of the baseline's bytes rounded down, a baseline of nothing counting
    // one byte). Without a confidence the bound is the lower median of the tasks. With one, the exact sign test's lower bound of
    // the median: at a spacing of a burst or more, of the tasks; closer, each cluster counts its minimum, and the bound is the
    // lesser of the even clusters' and the odd ones', each set a burst apart and so independent (review F05, H1: clusters'
    // medians tested together passed 5.9% at 95%; eng/trial-statistics/cost_bursts.py holds the level). No bound: one unit under
    // the target. Met exactly when the bound reaches the target, so a verifier checks the shown value with no statistics.
    /// <summary>
    /// The context the pack saves against the arm without it, task by task (feature 05's rule): the median saving in bytes and,
    /// at <paramref name="confidence"/>, its exact sign-test lower bound, for a report that names no cost target (feature 14).
    /// </summary>
    /// <exception cref="ArgumentException">An arm does not carry each task's offered bytes.</exception>
    public static (double Median, double Bound) Saving(PackTrialMeasurement with, PackTrialMeasurement without, double confidence)
    {
        var check = CostCheck("savingBytes", 0, confidence, with, without, 0);
        return (check.Estimate ?? check.Observed, check.Observed);
    }

    private static PackTrialCheck CostCheck(string target, double required, double? confidence, PackTrialMeasurement with, PackTrialMeasurement without, long fixedBytes)
    {
        var (a, b) = Paired(with, without);
        if (a.Any(t => t.OfferedBytes is null) || b.Any(t => t.OfferedBytes is null))
            throw new ArgumentException($"{target} needs each task's offered bytes on both arms (PerTask.OfferedBytes).");
        var bytes = target == "savingBytes";
        var values = new long[a.Count];
        for (var i = 0; i < a.Count; i++)
        {
            var saving = b[i].OfferedBytes!.Value - a[i].OfferedBytes!.Value - fixedBytes;
            values[i] = bytes ? saving : FloorDivide(checked(saving * PackNumbers.One), Math.Max(1, b[i].OfferedBytes!.Value));
        }
        var m = Math.Max(ClusterSize(with), ClusterSize(without));
        var least = bytes ? checked((long)required) : checked((long)Math.Round(required * PackNumbers.One));
        var estimate = LowerMedian(values);
        var bound = estimate;
        if (confidence is { } c)
        {
            var permille = (int)Math.Round(c * 1000);
            var units = m == 1 ? values : [.. values.Chunk(m).Select(cluster => cluster.Min())];
            var halves = m == 1 ? [SignBound(units, permille)]
                : new[] { SignBound([.. units.Where((_, i) => i % 2 == 0)], permille), SignBound([.. units.Where((_, i) => i % 2 == 1)], permille) };
            bound = halves.Any(h => h is null) ? least - 1 : halves.Min(h => h!.Value);
        }
        double Shown(long v) => bytes ? v : v / (double)PackNumbers.One;
        return new(target, required, Shown(bound), bound >= least, confidence is null ? null : Shown(estimate));
    }

    // The exact sign test's lower bound of the median of independent units (R-TRIAL-056, exact.sign_bound): the k-th largest, k
    // the fewest of n whose Clopper-Pearson bound reaches one half at the confidence; null when not even n of n does.
    private static long? SignBound(long[] units, int permille)
    {
        var n = units.Length;
        if (n == 0 || !MeetsRate(n, n, permille, Q.Of(1, 2))) return null;
        var (lo, hi) = (1, n);
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (MeetsRate(mid, n, permille, Q.Of(1, 2))) hi = mid; else lo = mid + 1;
        }
        return units.Order().ElementAt(n - lo);
    }

    private static long FloorDivide(long a, long b) => a / b - (a % b != 0 && (a < 0) != (b < 0) ? 1 : 0);

    private static long LowerMedian(long[] values)
    {
        if (values.Length == 0) return 0;
        var sorted = values.Order().ToArray();
        return sorted[(sorted.Length - 1) / 2];
    }

    // An arm's hit rate and pooled precision as exact ratios of its counts (0 for none), as the estimates are defined.
    private static Q RateOf(PackTrialMeasurement arm) => arm.Tasks == 0 ? 0 : Q.Of(arm.Hits, arm.Tasks);

    private static Q PrecisionOf(PackTrialMeasurement arm) => arm.Offered == 0 ? 0 : Q.Of(arm.Correct, arm.Offered);

    private static PackTrialMeasurement Arm(PackTrialMeasurement? arm, string target) =>
        arm ?? throw new ArgumentException($"{target} needs an arm the record does not carry.");

    private static long Micros(double milliseconds) => (long)Math.Round(milliseconds * 1000, MidpointRounding.AwayFromZero);

    // The most items the arm with the pack offers a task: the pack's maxItems, fixed before it ran.
    private static int DeclaredCap(ModuleManifest manifest) => manifest.Traversal?.MaxItems ?? ModuleTraversal.MaximumOffered;

    // An arm without a recorded cap takes the declared one: the pack's maxItems for the arm with it, 0 for a baseline of none,
    // which offers nothing by design (a host baseline keeps the widest, 16, which only widens a difference's bound).
    private static (PackTrialMeasurement With, PackTrialMeasurement Without) Declared(ModuleManifest manifest, PackTrialMeasurement with, PackTrialMeasurement without) =>
        (with.MaximumOffered is null ? with with { MaximumOffered = DeclaredCap(manifest) } : with,
         without.MaximumOffered is null && (manifest.Trial?.Baseline ?? "none") == "none" ? without with { MaximumOffered = 0 } : without);

    /// <summary>
    /// Why an arm's cap is not the one the declarations fix, or null (review 7, G4: every hedged bound rests on the cap, and it
    /// was taken as recorded; a cap of 1 on a pack of maxItems 16 bounded precision at 0.613 where the statistic gives 0.290,
    /// and a host baseline recorded at 0 lost its hedge): the arm with the pack at the pack's maxItems, a baseline of none at 0
    /// with nothing offered, a host baseline at 1 or more. A decision and an admission from a certificate check it alike.
    /// </summary>
    internal static (string Problem, string Arm)? CapProblem(ModuleManifest manifest, PackTrialMeasurement with, PackTrialMeasurement? without)
    {
        var cap = DeclaredCap(manifest);
        if ((with.MaximumOffered ?? ModuleTraversal.MaximumOffered) != cap)
            return ($"The arm with the pack records a cap of {with.MaximumOffered}; the pack offers at most {cap} items a task (its maxItems).", "with");
        if (without is null) return null;
        if ((manifest.Trial?.Baseline ?? "none") == "none")
            return without.MaximumOffered == 0 && without.Offered == 0 ? null : ("A baseline of none offers nothing: its arm records a cap of 0 and no item.", "without");
        return without.MaximumOffered == 0 ? ("A baseline of host is the host's own retrieval: its arm records a cap of 1 or more.", "without") : null;
    }

    // The one intake every verdict passes, when a decision is made and at every later check of it (review 7, G5 and G13: the
    // recheck skipped what the decision refused, and the arms of one trial could record different spacings, or a spacing no
    // history holds): both arms ran the same tasks; each arm is consistent in itself; every arm was drawn at one spacing,
    // within the history window.
    private static (string Problem, string Arm)? Intake(PackTrialMeasurement with, PackTrialMeasurement? without, PackTrialMeasurement? previous)
    {
        if (without is not null && without.Tasks != with.Tasks) return ("Both arms run the same tasks.", "without");
        if (Inconsistency(with) is { } a) return (a, "with");
        if (without is not null && Inconsistency(without) is { } b) return (b, "without");
        if (previous is not null && Inconsistency(previous) is { } c) return ("The previous version's measurement: " + c, "previous");
        var spacing = with.Spacing ?? 1;
        if (without is not null && (without.Spacing ?? 1) != spacing || previous is not null && (previous.Spacing ?? 1) != spacing)
            return ("Every arm of one trial was drawn at one spacing: they ran the same tasks.", "without");
        if (with.Tasks > 1 && (long)spacing * (with.Tasks - 1) >= HistoryWindow)
            return ($"{with.Tasks} tasks drawn every {spacing} eligible commits span more than the {HistoryWindow} a trial draws from.", "with");
        return null;
    }

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
        // Each task's bytes (feature 05 review, M1 and L1): 0 to 10^9, on every task or none, and none offered by an arm that
        // offers nothing by design.
        if (tasks.Any(x => x?.OfferedBytes is < 0 or > MaximumTaskBytes) || tasks.Any(x => x?.OfferedBytes is null) && tasks.Any(x => x?.OfferedBytes is not null)
            || arm.MaximumOffered == 0 && tasks.Any(x => x?.OfferedBytes is > 0))
            return $"PerTask's OfferedBytes are 0 to {MaximumTaskBytes} on every task or none, and 0 on an arm that offers nothing.";
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
            // A partial decision holds while one admitted part does, each rechecked from its own evidence (feature 11).
            if (decision.Parts is not null)
                return decision.Active && HeldParts(decision) is { Count: 0 } ? "no admitted part holds under its own evidence" : null;
            if (decision.With is not { } with) return "its record carries no measurement";
            if (with.Tasks < ModuleTrial.MinimumTasks) return $"only {with.Tasks} tasks; a trial needs {ModuleTrial.MinimumTasks}";
            IReadOnlyList<PackTrialCheck> again;
            try { again = Recheck(decision); }
            catch (ArgumentException e) { return $"its checks cannot be derived from what the record carries: {e.Message}"; }
            return again.FirstOrDefault(c => !c.Met) is { } failed
                ? $"{failed.Target} {failed.Observed.ToString(CultureInfo.InvariantCulture)} {(PackRegistry.AtMost.Contains(failed.Target) ? "above" : "below")} {failed.Required.ToString(CultureInfo.InvariantCulture)}"
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
        if (Intake(with, decision.Without, decision.Previous?.With) is { } refused) throw new ArgumentException(refused.Problem, nameof(decision));
        return [.. decision.Checks.Select(c => Check(c.Target, c.Required, decision.Confidence, with, decision.Without, decision.Previous?.With, decision.Safety, decision.FixedBytes))];
    }

    /// <summary>
    /// The registrations that may compose, and the ones left off: a module that declares a trial needs a passing decision for
    /// exactly its manifest and, when it came from a pack, exactly that pack; a decision the host made from a certificate counts
    /// only in a namespace where it trusts certificates, and elsewhere its latest own trial decides; in a namespace where the
    /// host made trials mandatory, a module that declares none is left off; a module that needs one left off is left off too.
    /// </summary>
    internal static (IReadOnlyList<ModuleRegistration> On, IReadOnlyList<SwitchedOffModule> Off) Apply(IReadOnlyList<ModuleRegistration> registrations,
        IPackActivations? activations, PackTrialPolicy? policy = null, DateTimeOffset? now = null, IReadOnlySet<string>? dark = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        // A module running dark offers nothing and is what its own trial runs on: the gate composes it and does not judge it
        // (feature 11; review 11-13, H1).
        bool Judged(ModuleRegistration r) => dark?.Contains(r.Manifest.Id) != true;
        if (activations is null) return (registrations, []);
        policy ??= PackTrialPolicy.None;
        var (mandatory, certificateTrust, floor) = (policy.Mandatory, policy.CertificateTrust, policy.Floor);
        var off = new Dictionary<string, SwitchedOffModule>(StringComparer.Ordinal);
        foreach (var registration in registrations.Where(r => r.Manifest.Trial is null && Judged(r)))
            if ((mandatory ?? []).FirstOrDefault(p => ModuleSetBuilder.Covers(p, registration.Manifest.Id)) is { } prefix)
                off[registration.Manifest.Id] = new SwitchedOffModule(registration.Manifest.Id, registration.Manifest.Version.ToString(),
                    $"it declares no trial, and this host requires one for {prefix}");
        foreach (var registration in registrations.Where(r => r.Manifest.Trial is not null && Judged(r)))
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
                    : floor is not null && Below(decision, floor) is { } short_ ? $"its trial showed less than this host requires: {short_}"
                    : Upgrade(activations, manifest, decision, floor))
                : setAside is not null ? setAside
                : activations.Find(manifest.Id, manifest.Version) is not null ? "its trial ran on other content; trial it again"
                : "no trial on this host yet";
            // A decision counts for as many days as the host's floor says (feature 11, R1): past them, off until trialled again;
            // within them, the module goes silent when they end, checked as the host runs.
            if (reason is null && decision is not null && floor?.ExpireAfterDays is { } days)
            {
                // From the evidence's own time: a decision made from a certificate ages from the evaluation (review 11-13).
                var expires = (decision.Basis?.EvaluatedAt ?? decision.DecidedAt).AddDays(days);
                if (at > expires) reason = $"its trial decision of {decision.DecidedAt.ToUniversalTime():yyyy-MM-dd} expired after {days} days, as this host requires; trial it again";
                else registration.DecisionExpires = expires;
            }
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

    // What a trial claims short of the host's floor, or null: the confidence, the tasks, each target the floor names, and a lift
    // only against the host's own retrieval. A trial that claims less can never show enough; one that claims enough is judged
    // again on what it showed (the overload below), which decides.
    internal static string? Below(ModuleTrial trial, PackTrialFloor floor)
    {
        var gaps = new List<string>();
        static string S(double v) => v.ToString(CultureInfo.InvariantCulture);
        if (Level(floor.ConfidencePermille) is { } least)
        {
            if (Level(trial.ConfidencePermille) is not { } declared) gaps.Add($"no confidence, {S(least)} required");
            else if (declared < least) gaps.Add($"confidence {S(declared)} below {S(least)}");
        }
        if (trial.Tasks < floor.Tasks) gaps.Add($"{trial.Tasks} tasks, {floor.Tasks} required");
        if (floor.Targets is { LiftMicro: not null } or { PrecisionLiftMicro: not null } && trial.Baseline != "host")
            gaps.Add($"its lifts are against {trial.Baseline}, not this host's own retrieval");
        if (floor.Targets is { } f)
        {
            var t = trial.Targets;
            void AtLeast(string name, double? required, double? declared)
            {
                if (required is not { } r) return;
                if (declared is not { } d) gaps.Add($"no {name} target, {S(r)} required");
                else if (d < r) gaps.Add($"{name} {S(d)} below {S(r)}");
            }
            AtLeast("hitRate", F(f.HitRateMicro), F(t.HitRateMicro));
            AtLeast("precision", F(f.PrecisionMicro), F(t.PrecisionMicro));
            AtLeast("lift", F(f.LiftMicro), F(t.LiftMicro));
            AtLeast("precisionLift", F(f.PrecisionLiftMicro), F(t.PrecisionLiftMicro));
            AtLeast("noHarm", F(f.NoHarmMicro), F(t.NoHarmMicro));
            if (f.AddedP95Ms is { } budget)
            {
                if (t.AddedP95Ms is not { } added) gaps.Add($"no addedP95Ms target, {S(budget)} required");
                else if (added > budget) gaps.Add($"addedP95Ms {S(added)} above {S(budget)}");
            }
        }
        if (floor.Safety is { } safe)
        {
            if (trial.Safety is not { } declared) gaps.Add($"no safety target, one on {safe.Probes} required");
            else if (declared.Probes != safe.Probes) gaps.Add($"safety on {declared.Probes}, {safe.Probes} required");
            else if (declared.MaxFollowMicro > safe.MaxFollowMicro) gaps.Add($"safety {S(PackNumbers.Fraction(declared.MaxFollowMicro))} above {S(PackNumbers.Fraction(safe.MaxFollowMicro))}");
        }
        return gaps.Count == 0 ? null : string.Join("; ", gaps);
    }

    /// <summary>
    /// What a decision's evidence holds short of the host's floor, or null (review 7, G1 and G7: the floor read the declaration,
    /// so a pack declaring 200 tasks met a floor of 100 on a history of 12, and a lift measured against nothing met a floor that
    /// meant "better than what this host has"): the tasks it ran, the confidence it was judged at, each target the floor names
    /// checked at least as strictly, and a lift only against an arm that could offer something. The gate and the upgrade chain
    /// both judge with it, so "passed here" means one thing (review 7, G8).
    /// </summary>
    internal static string? Below(PackActivation decision, PackTrialFloor floor)
    {
        var gaps = new List<string>();
        static string S(double v) => v.ToString(CultureInfo.InvariantCulture);
        if (Level(floor.ConfidencePermille) is { } least)
        {
            if (decision.Confidence is not { } judged) gaps.Add($"judged on estimates, {S(least)} required");
            else if (judged < least) gaps.Add($"judged at {S(judged)}, below {S(least)}");
        }
        var ran = decision.With?.Tasks ?? 0;
        if (ran < floor.Tasks) gaps.Add($"{ran} tasks ran, {floor.Tasks} required");
        if (floor.Targets is { } f)
        {
            double? Checked(string target) => decision.Checks.FirstOrDefault(c => c.Target == target)?.Required;
            void AtLeast(string name, double? required)
            {
                if (required is not { } r) return;
                if (Checked(name) is not { } d) gaps.Add($"no {name} checked, {S(r)} required");
                else if (d < r) gaps.Add($"{name} checked at {S(d)}, below {S(r)}");
            }
            AtLeast("hitRate", F(f.HitRateMicro));
            AtLeast("precision", F(f.PrecisionMicro));
            AtLeast("lift", F(f.LiftMicro));
            AtLeast("precisionLift", F(f.PrecisionLiftMicro));
            AtLeast("noHarm", F(f.NoHarmMicro));
            if (f.AddedP95Ms is { } budget)
            {
                if (Checked("addedP95Ms") is not { } added) gaps.Add($"no addedP95Ms checked, {S(budget)} required");
                else if (added > budget) gaps.Add($"addedP95Ms checked at {S(added)}, above {S(budget)}");
            }
            if ((F(f.LiftMicro) is not null || F(f.PrecisionLiftMicro) is not null) && decision.Without?.MaximumOffered == 0)
                gaps.Add("its lifts are against a baseline that offers nothing, not this host's own retrieval");
        }
        if (floor.Safety is { } safe)
        {
            if (decision.Safety is not { } run || run.Suite != safe.Probes) gaps.Add($"no run of {safe.Probes} kept");
            else if (decision.Checks.FirstOrDefault(c => c.Target == "safety") is not { } check) gaps.Add("no safety checked");
            else if (check.Required > PackNumbers.Fraction(safe.MaxFollowMicro)) gaps.Add($"safety checked at {S(check.Required)}, above {S(PackNumbers.Fraction(safe.MaxFollowMicro))}");
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

    // A decision made from a certificate never counts as a version that passed here (review 1, M2, outside certificate trust;
    // review 8, G2, inside it too: the gate counts one only while its certificate is the latest trusted one the pack carries,
    // which only the composing registration can tell, so the chain, which loads no predecessor, counted ones the gate set aside).
    private static string? Upgrade(IPackActivations activations, ModuleManifest manifest, PackActivation decision, PackTrialFloor? floor) =>
        Settle(activations, decision, Rule(decision.Upgrade ?? manifest.Trial?.Upgrade, decision.Confidence),
            new Dictionary<PackActivation, (string?, HeldRule)>(ReferenceEqualityComparer.Instance), floor).Refusal;

    // One no-regression rule a version declared: its tolerance, and the confidence its bound is taken at (none: the estimate).
    private sealed record NoRegressionRule(double Tolerance, double? Confidence);

    // Every rule an upgrade is held to, each checked as it was declared (review 5, M6: the confidence travels with its rule;
    // review 6: two rules are not ordered, so the stricter of two was a blend that could be looser than either, a tolerance of
    // 0 checked on the estimate in place of a predecessor's bound at 0.1). Empty: no regression was never asked for.
    private sealed record HeldRule(IReadOnlyList<NoRegressionRule> Rules);

    private static HeldRule Rule(ModuleTrialUpgrade? upgrade, double? confidence) =>
        new(upgrade is { NoRegression: true } u ? [new NoRegressionRule(PackNumbers.Fraction(u.ToleranceMicro), confidence)] : []);

    // Why a passing decision may not go on as an upgrade (or null), and the rule it is held to: the stricter of its own and
    // the one its predecessor was held to, so a rule carries down the whole chain, not one version (third review). The version
    // it replaces is the one that passed here before it: of every content's latest decision from this host's own trial (a later
    // failing one supersedes a pass; a certificate's never stands here, review 8, G2), a passing one that still holds under this library's statistic and that the gate itself allows, at an earlier
    // version, or at this version with other content decided earlier (on the same instant, the lower digest first); the
    // highest, the latest.
    private static (string? Refusal, HeldRule Rule) Settle(IPackActivations activations, PackActivation decision, HeldRule rule,
        Dictionary<PackActivation, (string?, HeldRule)> known, PackTrialFloor? floor)
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
            .Select(g => Counting(g, certificatesCount: false))
            .OfType<PackActivation>()
            .Select(a => (Decision: a, Version: ModuleVersion.TryParse(a.Version, out var v) ? v : null))
            .Where(a => a.Decision.Active && Unmet(a.Decision) is null && (floor is null || Below(a.Decision, floor) is null) && a.Version is not null && Content(a.Decision) != content
                && (a.Version.CompareTo(version) < 0 || a.Version.CompareTo(version) == 0
                    && (a.Decision.DecidedAt < decision.DecidedAt || a.Decision.DecidedAt == decision.DecidedAt && string.CompareOrdinal(Content(a.Decision), content) < 0)))
            .OrderByDescending(a => a.Version).ThenByDescending(a => a.Decision.DecidedAt).ThenByDescending(a => Content(a.Decision), StringComparer.Ordinal)
            .Select(a => (a.Decision, Settled: Settle(activations, a.Decision, Rule(a.Decision.Upgrade, a.Decision.Confidence), known, floor)))
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
        // The version that passed was judged here at its declared cap; its arm in the comparison is held to that cap, never taken
        // as recorded (review 7, PREVIOUS-CAP: a narrower cap tightens the bound, and the host may no longer hold that version's
        // declarations, but it holds its own decision about it).
        var judged = active.With?.MaximumOffered ?? ModuleTraversal.MaximumOffered;
        if ((previous.With.MaximumOffered ?? ModuleTraversal.MaximumOffered) != judged)
            return $"its trial records {active.Version} at a cap of {previous.With.MaximumOffered}; this host judged {active.Version} at {judged}";
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
