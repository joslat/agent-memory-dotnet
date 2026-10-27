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
    /// The lower confidence bound of a ratio of items over tasks, with each task as one cluster (PACK-gate-stats-2): the
    /// cluster-robust variance of the ratio gives a design effect, the item count divided by it is the effective sample size,
    /// and the Wilson bound is taken at that size. Items in one task are correlated, so this is never above the plain bound.
    /// </summary>
    public static double ClusteredWilsonLower(IReadOnlyList<PackTrialTask> tasks, double z)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var offered = tasks.Sum(t => (double)t.Offered);
        if (offered == 0) return 0;
        var p = tasks.Sum(t => (double)t.Correct) / offered;
        var clusters = tasks.Count(t => t.Offered > 0);
        // One cluster carries no information about the spread between tasks: it counts as one observation.
        if (clusters < 2) return WilsonLower(p, 1, z);
        var residuals = tasks.Sum(t => Math.Pow(t.Correct - p * t.Offered, 2));
        var clustered = clusters / (clusters - 1.0) * residuals / (offered * offered);
        var binomial = p * (1 - p) / offered;
        var designEffect = binomial == 0 ? 1 : Math.Max(1, clustered / binomial);
        return WilsonLower(p, offered / designEffect, z);
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
    /// <exception cref="ArgumentException">The manifest declares no trial, or <paramref name="packDigest"/> is not <c>sha256:</c> and 64 hex digits.</exception>
    public static PackActivation Decide(ModuleManifest manifest, PackTrialMeasurement with, PackTrialMeasurement without, DateTimeOffset decidedAt,
        string? packDigest = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (packDigest is not null && !PackDigestPattern.IsMatch(packDigest))
            throw new ArgumentException("A pack digest is sha256: and 64 lowercase hex digits.", nameof(packDigest));
        ArgumentNullException.ThrowIfNull(with);
        if (with.PerTask is { } perTask && (perTask.Count != with.Tasks || perTask.Sum(t => t.Offered) != with.Offered
                || perTask.Sum(t => t.Correct) != with.Correct || perTask.Any(t => t.Correct < 0 || t.Correct > t.Offered)))
            throw new ArgumentException("PerTask holds one entry per task, 0 <= correct <= offered, summing to Offered and Correct.", nameof(with));
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
        };
    }

    /// <summary>
    /// The registrations that may compose, and the ones left off: a module that declares a trial needs a passing decision for
    /// exactly its manifest and, when it came from a pack, exactly that pack; in a namespace where the host made trials
    /// mandatory, a module that declares none is left off; a module that needs one left off is left off too.
    /// </summary>
    internal static (IReadOnlyList<ModuleRegistration> On, IReadOnlyList<SwitchedOffModule> Off) Apply(IReadOnlyList<ModuleRegistration> registrations,
        IPackActivations? activations, IReadOnlyList<string>? mandatory = null)
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
            var decision = activations.Find(manifest.Id, manifest.Version, DigestOf(manifest), registration.PackDigest);
            var reason = decision is not null ? (decision.Active ? null : "its trial failed: " + decision.Reason)
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
                var id = (activation.ModuleId, activation.Version);
                if (!_latest.TryGetValue(id, out var known) || known.DecidedAt < activation.DecidedAt) _latest[id] = activation;
                var exact = (activation.ModuleId, activation.Version, activation.ManifestDigest, activation.PackDigest ?? "");
                if (!_latestByDigest.TryGetValue(exact, out var same) || same.DecidedAt < activation.DecidedAt) _latestByDigest[exact] = activation;
            }
        Ignored = ignored.AsReadOnly();
    }

    /// <summary>Files that were not valid records signed by the host key.</summary>
    public IReadOnlyList<string> Ignored { get; }

    /// <inheritdoc/>
    public PackActivation? Find(string moduleId, ModuleVersion version) => _latest.GetValueOrDefault((moduleId, version.ToString()));

    /// <inheritdoc/>
    public PackActivation? Find(string moduleId, ModuleVersion version, string manifestDigest, string? packDigest = null) =>
        _latestByDigest.GetValueOrDefault((moduleId, version.ToString(), manifestDigest, packDigest ?? ""));

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
            var verified = root.GetProperty("signatures").EnumerateArray().Any(s =>
                key.VerifyData(Pae(payload), Convert.FromBase64String(s.GetProperty("sig").GetString()!), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
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
