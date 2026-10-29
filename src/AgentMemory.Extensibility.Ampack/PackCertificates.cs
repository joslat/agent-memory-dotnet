using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Packs.Format;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>
/// A pack's card (pack format v1.2 §15), as its signed <c>card.json</c> states it: who owns the pack, what it is for and not
/// for, where its content comes from, the boundaries a host must keep, and the risks its publisher knows.
/// </summary>
[Experimental("AMEXT001")]
public sealed record PackCardInfo
{
    /// <summary>Who owns and answers for the pack.</summary>
    public required string Owner { get; init; }
    /// <summary>How to reach the owner, as text; null when not given.</summary>
    public string? OwnerContact { get; init; }
    /// <summary>What the pack is for.</summary>
    public required string IntendedUse { get; init; }
    /// <summary>What it is not for, and where it may be wrong.</summary>
    public required string Limitations { get; init; }
    /// <summary>What a host must not do with it.</summary>
    public IReadOnlyList<string> Boundaries { get; init; } = [];
    /// <summary>The risks its publisher knows.</summary>
    public IReadOnlyList<string> Risks { get; init; } = [];
    /// <summary>Where its content comes from; null when not given.</summary>
    public string? Provenance { get; init; }
    /// <summary>Who it is meant for; null when not given.</summary>
    public string? Audience { get; init; }
    /// <summary>Its domain; null when not given.</summary>
    public string? Domain { get; init; }
    /// <summary>Further reading.</summary>
    public IReadOnlyList<string> References { get; init; } = [];

    /// <summary>The texts <c>ampack init</c> writes into a new card, to be replaced before the pack travels.</summary>
    internal const string TemplateOwner = "Who owns this pack", TemplateUse = "Describe what this pack is for.",
        TemplateLimitations = "Describe what it is not for, and where it may be wrong.";

    /// <summary>Whether it still holds the texts <c>ampack init</c> wrote.</summary>
    public bool IsTemplate => Owner == TemplateOwner || IntendedUse == TemplateUse || Limitations == TemplateLimitations;

    internal static PackCardInfo? Of(IReadOnlyDictionary<string, byte[]> files) =>
        files.TryGetValue(PackCard.FileName, out var bytes) && PackCard.Parse(bytes).Card is { } card
            ? new PackCardInfo
            {
                Owner = card.OwnerName, OwnerContact = card.OwnerContact, IntendedUse = card.IntendedUse, Limitations = card.Limitations,
                Boundaries = card.Boundaries, Risks = card.Risks, Provenance = card.Provenance, Audience = card.Audience, Domain = card.Domain,
                References = card.References,
            }
            : null;
}

/// <summary>
/// An evaluation certificate a pack carries (pack format v1.2 §16), as this host read it: who evaluated, on what, with what
/// result, and whether this host trusts it (a valid signature by a key its trust policy names as evaluator for the pack's id).
/// A certificate informs; it admits a pack only where the host trusts certificates (<see cref="PackCertificates.Admit"/> and
/// <see cref="ModuleSetBuilder.TrustCertificatesFor"/>).
/// </summary>
[Experimental("AMEXT001")]
public sealed record PackEvaluation
{
    /// <summary>The certificate itself: <c>sha256:</c> over its envelope's exact bytes.</summary>
    public string Digest { get; internal init; } = null!;
    /// <summary>Its file under <c>META/attestations/</c>.</summary>
    public string File { get; internal init; } = null!;
    /// <summary>Whether this host trusts it: signed by a key it names as evaluator for this pack's id, for exactly this pack.</summary>
    public bool Trusted { get; internal init; }
    /// <summary>Why it is not trusted (empty when it is).</summary>
    public IReadOnlyList<string> Problems { get; internal init; } = [];
    /// <summary>Who evaluated, as the certificate names it (null when it could not be read).</summary>
    public string? Evaluator { get; internal init; }
    /// <summary>The key that signed it, when trusted.</summary>
    public string? EvaluatorKey { get; internal init; }
    /// <summary>When it was evaluated.</summary>
    public DateTimeOffset? EvaluatedAt { get; internal init; }
    /// <summary>The evaluating tool and its version.</summary>
    public string? Tool { get; internal init; }
    /// <summary>The trial's measure, baseline and confidence (null when none was asked).</summary>
    public string? Measure { get; internal init; }
    /// <summary>What "without the pack" meant: <c>host</c> or <c>none</c>.</summary>
    public string? Baseline { get; internal init; }
    /// <summary>The confidence a rate target's bound was taken at, when the trial asked for one.</summary>
    public double? Confidence { get; internal init; }
    /// <summary>Where the evaluator's tasks came from.</summary>
    public string? TaskSource { get; internal init; }
    /// <summary>Every check, as the gate records it.</summary>
    public IReadOnlyList<PackTrialCheck> Checks { get; internal init; } = [];
    /// <summary>The arm with the pack.</summary>
    public PackTrialMeasurement? With { get; internal init; }
    /// <summary>The arm without it.</summary>
    public PackTrialMeasurement? Without { get; internal init; }
    /// <summary>Whether every target was met on enough tasks.</summary>
    public bool Passed { get; internal init; }
    /// <summary>The AEF run it summarises: AEF's run id and run hash; null when it names none.</summary>
    public string? AefRunId { get; internal init; }
    /// <summary>That run's run hash.</summary>
    public string? AefRunHash { get; internal init; }
    /// <summary>Whether that run travels in the pack, beside the certificate, and hashes to the run hash it names.</summary>
    public bool AefRunIncluded { get; internal init; }
}

/// <summary>
/// Evaluation certificates (pack format v1.2 §16): issuing one as an evaluator, reading the ones a pack carries, and admitting
/// a pack on one as a host that chose certificate trust for its namespace. The certificate is a signed summary of a trial the
/// evaluator ran on its own tasks (the gate's decision, and the AEF run AgentEval sealed from it); a host relies on it in
/// place of its own trial only where it says so, and only when the certificate covers exactly the trial the pack declares.
/// </summary>
[Experimental("AMEXT001")]
public static class PackCertificates
{
    /// <summary>The kind of decision basis an admission records.</summary>
    public const string CertificateBasis = "certificate";

    /// <summary>
    /// Adds an evaluator's certificate to a built pack, from the evaluator's own trial decision for exactly this pack: its
    /// checks, its arms, the trial's measure, baseline and confidence. With <paramref name="aefRunFolder"/>, the AEF run of that
    /// decision (as <c>ampack evidence</c> wrote it and AgentEval sealed it) travels beside the certificate, named by its run id
    /// and run hash. The pack's digest does not change. Returns the new archive.
    /// </summary>
    /// <exception cref="PackLoadException">The pack does not verify under <paramref name="trust"/>, or the decision is not for it.</exception>
    public static byte[] Issue(byte[] archive, PackTrust trust, PackActivation decision, string evaluatorPrivateKeyPem, string evaluator, string taskSource,
        string? aefRunFolder = null, string tool = "agentmemory-pack-gate", string toolVersion = "2.0.0")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evaluatorPrivateKeyPem);
        using var signer = PackSigner.FromPem(evaluatorPrivateKeyPem);
        return Issue(archive, trust, decision, signer, evaluator, taskSource, aefRunFolder, tool, toolVersion);
    }

    internal static byte[] Issue(byte[] archive, PackTrust trust, PackActivation decision, PackSigner signer, string evaluator, string taskSource,
        string? aefRunFolder, string tool, string toolVersion)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentNullException.ThrowIfNull(decision);
        var pack = PackLoader.Load(archive, trust);
        if (pack.Manifest.Trial is not { } trial) throw new PackLoadException([$"{pack.Id} declares no trial: there is nothing to certify."]);
        var problems = new List<string>();
        if (decision.ModuleId != pack.Id || decision.Version != pack.Version) problems.Add($"The decision is for {decision.ModuleId} {decision.Version}, not {pack.Id} {pack.Version}.");
        if (decision.PackDigest != pack.Digest) problems.Add("The decision is for other content: its trial did not run this pack.");
        if (decision.ManifestDigest != PackTrialGate.DigestOf(pack.Manifest)) problems.Add("The decision is for other declarations than this pack's.");
        if (decision.Basis is not null) problems.Add("The decision was itself made from a certificate: only a trial can be certified.");
        if (decision.With is not { } with) problems.Add("The decision carries no measurement.");
        // The gate's decision for the declared trial checks every declared target at the declared value; a certificate made from
        // anything else would misstate what was measured.
        foreach (var (target, value) in Declared(trial))
            if (decision.Checks.FirstOrDefault(c => c.Target == target) is not { } check || Milli(check.Required) != Milli(value))
                problems.Add($"The decision does not check {target} at the declared {value.ToString(CultureInfo.InvariantCulture)}: it is not the gate's decision for this pack's trial.");
        if (problems.Count > 0) throw new PackLoadException(problems);

        var evaluatedAt = DateTimeOffset.FromUnixTimeSeconds(decision.DecidedAt.ToUnixTimeSeconds());
        var stem = PackAttestations.StemFor(signer.KeyId, evaluatedAt);
        var content = PackReader.ReadArchive(archive, "archive");
        // A certificate issued again under the same stem replaces the earlier one and its run, never mixes with it (review 1, L3).
        var files = content.Files.Where(f => f.Key != PackAttestations.CertificatePath(stem)
                && !f.Key.StartsWith(PackPaths.AttestationsFolder + stem + PackAttestations.RunSuffix, StringComparison.Ordinal))
            .ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        string? runId = null, runHash = null;
        if (aefRunFolder is not null)
        {
            var run = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(aefRunFolder, "*", SearchOption.AllDirectories))
                run[Path.GetRelativePath(aefRunFolder, file).Replace(Path.DirectorySeparatorChar, '/')] = File.ReadAllBytes(file);
            runId = PackAttestations.RunId(run) ?? throw new PackLoadException([$"{aefRunFolder} is not an AEF run: its run.json names no runId."]);
            runHash = PackAttestations.RunHash(run);
            foreach (var (path, bytes) in run) files[PackPaths.AttestationsFolder + stem + PackAttestations.RunSuffix + path] = bytes;
        }
        var certificate = new PackCertificate(pack.Id, pack.Version, pack.Digest, evaluator, tool, toolVersion, evaluatedAt, trial.Measure, trial.Baseline,
            trial.Confidence is { } c ? (int)Math.Round(c * 1000) : null, taskSource,
            [.. decision.Checks.Select(k => new CertificateCheck(k.Target, Milli(k.Required), Milli(k.Observed), k.Met, k.Estimate is { } e ? Milli(e) : null))],
            Arm(decision.With!), decision.Without is { } without ? Arm(without) : null, decision.Active, runId, runHash);
        byte[] envelope;
        try { envelope = certificate.Sign(signer); }
        catch (InvalidDataException e) { throw new PackLoadException([e.Message]); }
        files[PackAttestations.CertificatePath(stem)] = envelope;
        if (PackPaths.Problems(files.Keys).Concat(PackAttestations.Problems(files)).ToList() is { Count: > 0 } shape) throw new PackLoadException(shape);
        return StrictZip.Write(files, compress: true);
    }

    /// <summary>
    /// The certificate that decides for <paramref name="pack"/>: the latest one by a key this host trusts as evaluator for it,
    /// or null. Admission and the loader's record of which certificate a decision rests on both read it here (review 1, L16).
    /// </summary>
    internal static PackEvaluation? LatestTrusted(LoadedPack pack) =>
        pack.Certificates.Where(c => c.Trusted).OrderByDescending(c => c.EvaluatedAt).ThenBy(c => c.Digest, StringComparer.Ordinal).FirstOrDefault();

    /// <summary>
    /// The decision a host makes from a certificate, where it trusts certificates: from the latest trusted certificate, when it
    /// passed and covers exactly the trial the pack declares (the same measure, baseline and confidence; every declared target
    /// checked at the declared value and met; enough tasks). Null, with the reason, when it does not. Sign it with the host's key
    /// (<see cref="PackActivationFolder.Write"/>); it counts only where <see cref="ModuleSetBuilder.TrustCertificatesFor"/> covers the pack.
    /// </summary>
    public static (PackActivation? Decision, string Reason) Admit(LoadedPack pack, DateTimeOffset decidedAt)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (pack.Manifest.Trial is not { } trial) return (null, $"{pack.Id} declares no trial: it needs no admission.");
        if (pack.Certificates.Count == 0) return (null, $"{pack.Id} carries no evaluation certificate.");
        // The latest trusted certificate decides, as the latest decision does for a host's own trials: a newer failure is not
        // passed over for an older pass (review 1, L1).
        var certificate = LatestTrusted(pack);
        if (certificate is null) return (null, $"None of the {pack.Certificates.Count} certificate(s) {pack.Id} carries is from a key this host trusts as evaluator for it.");
        if (Covers(certificate, trial) is { } gap)
            return (null, $"The latest trusted certificate, {certificate.Evaluator}'s of {certificate.EvaluatedAt:u}, does not admit it: {gap}.");
        return (new PackActivation
        {
            ModuleId = pack.Manifest.Id, Version = pack.Manifest.Version.ToString(), ManifestDigest = PackTrialGate.DigestOf(pack.Manifest),
            PackDigest = pack.Digest, Active = true, DecidedAt = decidedAt, Checks = certificate.Checks, With = certificate.With, Without = certificate.Without,
            Upgrade = trial.Upgrade,
            Reason = $"certified by {certificate.Evaluator} on {certificate.EvaluatedAt:yyyy-MM-dd}: every target met on {certificate.With!.Tasks} tasks",
            Basis = new PackDecisionBasis
            {
                Kind = CertificateBasis, Certificate = certificate.Digest, Evaluator = certificate.Evaluator!, EvaluatorKey = certificate.EvaluatorKey!,
                EvaluatedAt = certificate.EvaluatedAt!.Value, TaskSource = certificate.TaskSource!, AefRunId = certificate.AefRunId, AefRunHash = certificate.AefRunHash,
            },
        }, "admitted on " + certificate.Evaluator + "'s certificate");
    }

    // Why a certificate does not cover the declared trial, or null when it does.
    private static string? Covers(PackEvaluation certificate, ModuleTrial trial)
    {
        if (!certificate.Passed) return "it did not pass";
        if (certificate.Measure != trial.Measure) return $"it measured {certificate.Measure}; the pack declares {trial.Measure}";
        if (certificate.Baseline != trial.Baseline) return $"it compared with {certificate.Baseline}; the pack declares {trial.Baseline}";
        if (Milli(certificate.Confidence) != Milli(trial.Confidence)) return "it was not judged at the confidence the pack declares";
        if ((certificate.With?.Tasks ?? 0) < ModuleTrial.MinimumTasks) return $"it ran fewer than {ModuleTrial.MinimumTasks} tasks";
        foreach (var (target, value) in Declared(trial))
        {
            if (certificate.Checks.FirstOrDefault(c => c.Target == target) is not { } check) return $"it does not check {target}";
            if (Milli(check.Required) != Milli(value)) return $"it checked {target} against {check.Required.ToString(CultureInfo.InvariantCulture)}, not the declared {value.ToString(CultureInfo.InvariantCulture)}";
            if (!check.Met) return $"{target} was not met";
        }
        return null;
    }

    // The targets a trial declares, by the names the gate checks them under.
    private static IEnumerable<(string Target, double Value)> Declared(ModuleTrial trial)
    {
        var t = trial.Targets;
        foreach (var (target, value) in new (string, double?)[] { ("hitRate", t.HitRate), ("precision", t.Precision), ("lift", t.Lift), ("precisionLift", t.PrecisionLift), ("addedP95Ms", t.AddedP95Ms) })
            if (value is { } v) yield return (target, v);
    }

    /// <summary>Every certificate a verified pack carries, as this host reads it, trusted or not.</summary>
    internal static IReadOnlyList<PackEvaluation> Read(VerifiedPack pack, TrustPolicy policy, DateTimeOffset now)
    {
        var evaluations = new List<PackEvaluation>();
        foreach (var (stem, envelope) in PackAttestations.Certificates(pack.Attestations))
        {
            var run = PackAttestations.Run(pack.Attestations, stem);
            var (certificate, signedBy, problems) = PackCertificate.Verify(envelope, pack, policy, now, run);
            var evaluation = new PackEvaluation
            {
                Digest = "sha256:" + FilesList.Hex(envelope), File = PackAttestations.CertificatePath(stem), Trusted = certificate is not null, Problems = problems,
                EvaluatorKey = signedBy, AefRunIncluded = certificate is not null && run is not null,
            };
            // An untrusted certificate is still shown for what it claims, read strictly, never relied on.
            var claimed = certificate ?? Claimed(envelope);
            evaluations.Add(claimed is null ? evaluation : evaluation with
            {
                Evaluator = claimed.Evaluator, EvaluatedAt = claimed.EvaluatedAt, Tool = $"{claimed.Tool} {claimed.ToolVersion}", Measure = claimed.Measure,
                Baseline = claimed.Baseline, Confidence = claimed.ConfidenceMilli / 1000.0, TaskSource = claimed.TaskSource, Passed = claimed.Passed,
                Checks = [.. claimed.Checks.Select(c => new PackTrialCheck(c.Target, c.RequiredMilli / 1000.0, c.ObservedMilli / 1000.0, c.Met, c.EstimateMilli / 1000.0))],
                With = Measurement(claimed.With), Without = claimed.Without is { } w ? Measurement(w) : null, AefRunId = claimed.AefRunId, AefRunHash = claimed.AefRunHash,
            });
        }
        return evaluations;
    }

    // What an untrusted certificate claims, read in the JSON profile and strictly; null for anything else. Never throws: an
    // attestation is unsigned bytes anyone can add beside a pack, and it must not stop the pack from loading (review 1, H1).
    private static PackCertificate? Claimed(byte[] envelope)
    {
        if (envelope.Length > Dsse.MaximumEnvelopeBytes || PackJson.Check(envelope) is not null) return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(envelope);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && document.RootElement.TryGetProperty("payload", out var payload) && payload.ValueKind == System.Text.Json.JsonValueKind.String
                && Dsse.FromBase64(payload.GetString()!) is { } bytes ? PackCertificate.Read(bytes).Certificate : null;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException) { return null; }
    }

    private static PackTrialMeasurement Measurement(CertificateArm arm) =>
        new() { Tasks = arm.Tasks, Hits = arm.Hits, Offered = arm.Offered, Correct = arm.Correct, P95Ms = arm.P95Micros / 1000.0 };

    private static CertificateArm Arm(PackTrialMeasurement m) => new(m.Tasks, m.Hits, m.Offered, m.Correct, (long)Math.Round(m.P95Ms * 1000, MidpointRounding.AwayFromZero));

    private static long Milli(double value) => checked((long)Math.Round(value * 1000, MidpointRounding.AwayFromZero));

    private static long? Milli(double? value) => value is { } v ? Milli(v) : null;
}
