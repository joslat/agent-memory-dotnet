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
    /// The latest decision for exactly this content: id, version, manifest digest and, for a pack, its pack digest (null for a
    /// compiled module); null when it was never trialled.
    /// </summary>
    PackActivation? Find(string moduleId, ModuleVersion version, string manifestDigest, string? packDigest = null) =>
        Find(moduleId, version) is { } latest && latest.ManifestDigest == manifestDigest && latest.PackDigest == packDigest ? latest : null;

    /// <summary>
    /// Every decision for this pack id, any version (PACK-upgrade-gate: the version that passed before an upgrade). The default
    /// knows none, and then an upgrade's trial is not held to its predecessor.
    /// </summary>
    IReadOnlyList<PackActivation> History(string moduleId) => [];
}

/// <summary>A module the host left off, and why.</summary>
[Experimental("AMEXT001")]
public sealed record SwitchedOffModule(string ModuleId, string Version, string Reason);

/// <summary>Decides a trial, and applies decisions when a module set activates.</summary>
[Experimental("AMEXT001")]
public static class PackTrialGate
{
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

    /// <summary>
    /// The lower confidence bound of a ratio of items over tasks, with each task as one cluster (PACK-gate-stats-2): the lower
    /// of two bounds. Over items: the cluster-robust variance of the ratio gives a design effect, the item count divided by it
    /// is the effective sample size, and the Wilson bound is taken at that size. Over tasks: each task that offered anything is
    /// one observation scored by its own precision, and the Wilson bound is taken over them (valid for a mean of values in
    /// [0, 1]). The second keeps a few tasks from passing for many however many items one of them offered, and the two
    /// together change smoothly as one item turns right or wrong (third review: two tasks gave 0.425 at 200 of 200 right and
    /// 0.978 at 199). Items in one task are correlated, so this is never above the plain bound.
    /// </summary>
    public static double ClusteredWilsonLower(IReadOnlyList<PackTrialTask> tasks, double z)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var offered = tasks.Sum(t => (double)t.Offered);
        if (offered == 0) return 0;
        var p = tasks.Sum(t => (double)t.Correct) / offered;
        var answered = tasks.Where(t => t.Offered > 0).ToList();
        var overTasks = WilsonLower(answered.Average(t => (double)t.Correct / t.Offered), answered.Count, z);
        // One task, or a precision of 0 or 1, leaves no spread between tasks to measure a design effect by.
        if (answered.Count < 2 || p is 0 or 1) return overTasks;
        var residuals = tasks.Sum(t => Math.Pow(t.Correct - p * t.Offered, 2));
        var clustered = answered.Count / (answered.Count - 1.0) * residuals / (offered * offered);
        var designEffect = Math.Max(1, clustered / (p * (1 - p) / offered));
        return Math.Min(WilsonLower(p, offered / designEffect, z), overTasks);
    }

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
        if (with.PerTask is { } perTask && (perTask.Count != with.Tasks || perTask.Sum(t => t.Offered) != with.Offered
                || perTask.Sum(t => t.Correct) != with.Correct || perTask.Any(t => t.Correct < 0 || t.Correct > t.Offered)))
            throw new ArgumentException("PerTask holds one entry per task, 0 <= correct <= offered, summing to Offered and Correct.", nameof(with));
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
        if (t.Lift is { } lift) checks.Add(new("lift", lift, Math.Round(with.HitRate - without.HitRate, 3), with.HitRate - without.HitRate >= lift));
        if (t.PrecisionLift is { } precisionLift)
            checks.Add(new("precisionLift", precisionLift, Math.Round(with.Precision - without.Precision, 3), with.Precision - without.Precision >= precisionLift));
        if (t.AddedP95Ms is { } added) checks.Add(new("addedP95Ms", added, Math.Round(with.P95Ms - without.P95Ms, 1), with.P95Ms - without.P95Ms <= added));
        // An upgrade does not do worse than the version it replaces, on the same tasks, beyond the declared tolerance.
        if (trial.Upgrade is { NoRegression: true } upgrade && previous is not null)
        {
            var hitDrop = with.HitRate - previous.With.HitRate;
            var precisionDrop = with.Precision - previous.With.Precision;
            checks.Add(new("noRegression.hitRate", -upgrade.Tolerance, Math.Round(hitDrop, 3), hitDrop >= -upgrade.Tolerance - 1e-9));
            checks.Add(new("noRegression.precision", -upgrade.Tolerance, Math.Round(precisionDrop, 3), precisionDrop >= -upgrade.Tolerance - 1e-9));
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
            Previous = previous, Upgrade = trial.Upgrade,
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
        var active = activations.History(decision.ModuleId)
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
        var hitDrop = with.HitRate - previous.With.HitRate;
        var precisionDrop = with.Precision - previous.With.Precision;
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
    private readonly Dictionary<(string, string, string, string), PackActivation> _latestByDigest = [];
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
                var exact = (activation.ModuleId, activation.Version, activation.ManifestDigest, activation.PackDigest ?? "");
                if (!_latestByDigest.TryGetValue(exact, out var same) || same.DecidedAt < activation.DecidedAt) _latestByDigest[exact] = activation;
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
        _latestByDigest.GetValueOrDefault((moduleId, version.ToString(), manifestDigest, packDigest ?? ""));

    /// <inheritdoc/>
    public IReadOnlyList<PackActivation> History(string moduleId) => _history.TryGetValue(moduleId, out var all) ? all.AsReadOnly() : [];

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
        var file = Path.Combine(folder, $"{decision.ModuleId}@{decision.Version}-{decision.DecidedAt.UtcDateTime:yyyyMMddTHHmmssZ}{(decision.Basis is null ? "" : "-certificate")}.dsse.json");
        var statement = Statement(decision);
        var signature = hostKey.SignData(Pae(statement), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        File.WriteAllText(file, JsonSerializer.Serialize(new
        {
            payloadType = PayloadType, payload = Convert.ToBase64String(statement),
            signatures = new[] { new { keyid = "host", sig = Convert.ToBase64String(signature) } },
        }));
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
