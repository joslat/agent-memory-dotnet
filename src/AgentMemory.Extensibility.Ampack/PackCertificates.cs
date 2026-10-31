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
    /// <summary>Why it is not trusted; for a trusted one, what does not hold beside it (an AEF run that does not match).</summary>
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
    /// <summary>The statistic its checks were computed with; a host derives the verdict again under its own.</summary>
    public string? Statistic { get; internal init; }
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
        // Only a trial that measured each task as it stood before its change (review 4, L-4: the tool refused head, this did not):
        // a head trial sees the tests the changes created, and a record that does not say may have.
        if (decision.History != "parent")
            problems.Add(decision.History == "head"
                ? "The decision measured past changes against the repository as it is now (head), which sees the tests they created: trial with parent history before certifying."
                : "The decision does not say how its tasks were drawn: trial with parent history before certifying.");
        // The gate's decision for the declared trial checks every declared target at the declared value; a certificate made from
        // anything else would misstate what was measured.
        foreach (var (target, value) in Declared(trial))
            if (decision.Checks.FirstOrDefault(c => c.Target == target) is not { } check || Micro(check.Required) != Micro(value))
                problems.Add($"The decision does not check {target} at the declared {value.ToString(CultureInfo.InvariantCulture)}: it is not the gate's decision for this pack's trial.");
        // A target a certificate cannot state is not dropped from it (feature 04 review, M4: a decision that failed only on safety
        // was certified as passed): only each host's own probes decide safety.
        if (trial.Safety is not null) problems.Add("The trial declares a safety target, which only each host's own probes decide: no certificate states it.");
        // The evidence travels with the verdict (review 5, root RB): each arm's per-task results and cap, so every host derives
        // the verdict again under its own statistic; the checks are derived here under this one, never copied.
        if (new[] { decision.With, decision.Without }.OfType<PackTrialMeasurement>().Any(arm => arm.PerTask is null))
            problems.Add("The decision's arms carry no per-task results: a certificate carries the evidence a host checks again; trial it again.");
        // A certificate states exactly the declared targets, derived from the evidence it carries (review 7, G3: it copied an
        // upgrade trial's no-regression checks, whose previous arm it cannot carry, so no host could ever admit it; and a pack
        // that met every declared target was certified as failed because the evaluator's own predecessor did better). No
        // regression stays each host's own comparison, which its upgrade gate makes.
        var declaredTargets = Declared(trial).Select(d => d.Target).ToHashSet(StringComparer.Ordinal);
        var declared = decision with { Previous = null, Checks = [.. decision.Checks.Where(c => declaredTargets.Contains(c.Target))] };
        IReadOnlyList<PackTrialCheck> checks = [];
        if (problems.Count == 0)
        {
            try { checks = PackTrialGate.Recheck(declared); }
            catch (ArgumentException e) { problems.Add($"The decision's checks cannot be derived from what it carries: {e.Message}"); }
        }
        var enough = decision.With is { } measured && measured.Tasks >= ModuleTrial.MinimumTasks;
        var metThen = declared.Checks.All(c => c.Met);
        var passed = metThen && enough && checks.All(c => c.Met);
        if (problems.Count == 0 && !metThen && enough && checks.All(c => c.Met))
            problems.Add($"The decision failed, but its evidence meets every target under {PackTrialGate.Statistic}: a verdict is never given this way; trial it again.");
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
            // Read as a pack folder is: bounded per file and in all, no links (review 6, M2: read whole and through links).
            IReadOnlyDictionary<string, byte[]> run;
            try { run = PackReader.ReadFolder(aefRunFolder).Files; }
            catch (Exception e) when (e is InvalidDataException or IOException) { throw new PackLoadException([$"{aefRunFolder}: {e.Message}"]); }
            runId = PackAttestations.RunId(run) ?? throw new PackLoadException([$"{aefRunFolder} is not an AEF run: its run.json names no runId."]);
            // The run of this decision, not another version's (round 10, B10-M1).
            if (!PackAttestations.RunIsOf(run, pack.Id, pack.Digest, pack.Version))
                throw new PackLoadException([$"{aefRunFolder} is the evidence of another decision: its subject is not {pack.Id} {pack.Version} at {pack.Digest}."]);
            runHash = PackAttestations.RunHash(run);
            foreach (var (path, bytes) in run) files[PackPaths.AttestationsFolder + stem + PackAttestations.RunSuffix + path] = bytes;
        }
        var certificate = new PackCertificate(pack.Id, pack.Version, pack.Digest, evaluator, tool, toolVersion, evaluatedAt, trial.Measure, trial.Baseline,
            trial.ConfidencePermille, taskSource, PackTrialGate.Statistic,
            [.. checks.Select(k => new CertificateCheck(k.Target, Micro(k.Required), Micro(k.Observed), k.Met, k.Estimate is { } e ? Micro(e) : null))],
            Arm(decision.With!), decision.Without is { } without ? Arm(without) : null, passed, runId, runHash);
        byte[] envelope;
        try { envelope = certificate.Sign(signer); }
        catch (InvalidDataException e) { throw new PackLoadException([e.Message]); }
        files[PackAttestations.CertificatePath(stem)] = envelope;
        if (PackPaths.Problems(files.Keys).Concat(PackAttestations.Problems(files)).ToList() is { Count: > 0 } shape) throw new PackLoadException(shape);
        // The one writer, which reads its archive back before it returns it (review 7, F3).
        try { return PackBuilder.Pack(files); }
        catch (InvalidDataException e) { throw new PackLoadException([e.Message]); }
    }

    /// <summary>
    /// The certificate that decides for <paramref name="pack"/>: the latest one by a key this host trusts as evaluator for it,
    /// or null. Admission and the loader's record of which certificate a decision rests on both read it here (review 1, L16).
    /// </summary>
    internal static PackEvaluation? LatestTrusted(LoadedPack pack)
    {
        var trusted = pack.Certificates.Where(c => c.Trusted).ToList();
        if (trusted.Count == 0) return null;
        var latest = trusted.Max(c => c.EvaluatedAt);
        var tied = trusted.Where(c => c.EvaluatedAt == latest).OrderBy(c => c.Digest, StringComparer.Ordinal).ToList();
        // Two trusted evaluations of one second that disagree: the one that did not pass, so admission fails closed (review 6, M3:
        // which of a pass and a fail counted was the distributor's choice).
        return tied.FirstOrDefault(c => !c.Passed) ?? tied[0];
    }

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
        // The caps its evidence was bounded with are the ones this pack declares, as for the host's own trials (review 7, G4).
        if (PackTrialGate.CapProblem(pack.Manifest, certificate.With!, certificate.Without) is { } cap)
            return (null, $"The latest trusted certificate, {certificate.Evaluator}'s of {certificate.EvaluatedAt:u}, does not admit it: {cap.Problem}");
        var admitted = new PackActivation
        {
            ModuleId = pack.Manifest.Id, Version = pack.Manifest.Version.ToString(), ManifestDigest = PackTrialGate.DigestOf(pack.Manifest),
            PackDigest = pack.Digest, Active = true, DecidedAt = decidedAt, Checks = certificate.Checks, With = certificate.With, Without = certificate.Without,
            Upgrade = trial.Upgrade, FixedBytes = trial.Cost is null ? null : PackTrialRender.FixedBytes(pack.Manifest),
            Reason = $"certified by {certificate.Evaluator} on {certificate.EvaluatedAt:yyyy-MM-dd}: every target met on {certificate.With!.Tasks} tasks",
            Basis = new PackDecisionBasis
            {
                Kind = CertificateBasis, Certificate = certificate.Digest, Evaluator = certificate.Evaluator!, EvaluatorKey = certificate.EvaluatorKey!,
                EvaluatedAt = certificate.EvaluatedAt!.Value, TaskSource = certificate.TaskSource!, AefRunId = certificate.AefRunId, AefRunHash = certificate.AefRunHash,
            },
            Confidence = certificate.Confidence, Statistic = certificate.Statistic,
        };
        // The evaluator's verdict counts only where this host's own statistic, on the evidence the certificate carries, agrees.
        if (PackTrialGate.Unmet(admitted) is { } unmet)
            return (null, $"The latest trusted certificate, {certificate.Evaluator}'s of {certificate.EvaluatedAt:u}, does not admit it: its evidence does not meet its targets under {PackTrialGate.Statistic} ({unmet}).");
        return (admitted, "admitted on " + certificate.Evaluator + "'s certificate");
    }

    // Why a certificate does not cover the declared trial, or null when it does.
    private static string? Covers(PackEvaluation certificate, ModuleTrial trial)
    {
        if (!certificate.Passed) return "it did not pass";
        // A safety target is decided by this host's own probes (feature 04); no certificate carries one.
        if (trial.Safety is not null) return "the pack declares a safety target, which only this host's own probes decide";
        if (certificate.Measure != trial.Measure) return $"it measured {certificate.Measure}; the pack declares {trial.Measure}";
        if (certificate.Baseline != trial.Baseline) return $"it compared with {certificate.Baseline}; the pack declares {trial.Baseline}";
        if ((certificate.Confidence is { } judged ? Math.Round(judged * 1000) : (double?)null) != trial.ConfidencePermille)
            return "it was not judged at the confidence the pack declares";
        if ((certificate.With?.Tasks ?? 0) < ModuleTrial.MinimumTasks) return $"it ran fewer than {ModuleTrial.MinimumTasks} tasks";
        foreach (var (target, value) in Declared(trial))
        {
            if (certificate.Checks.FirstOrDefault(c => c.Target == target) is not { } check) return $"it does not check {target}";
            if (Micro(check.Required) != Micro(value)) return $"it checked {target} against {check.Required.ToString(CultureInfo.InvariantCulture)}, not the declared {value.ToString(CultureInfo.InvariantCulture)}";
            if (!check.Met) return $"{target} was not met";
        }
        return null;
    }

    // The targets a trial declares, by the names the gate checks them under.
    private static IEnumerable<(string Target, double Value)> Declared(ModuleTrial trial)
    {
        foreach (var target in trial.Targets.Checked()) yield return target;
        // The cost targets (feature 05), under the names the gate checks them: whole bytes, and a share in millionths.
        if (trial.Cost?.SavingBytesMedian is { } bytes) yield return ("savingBytes", bytes);
        if (trial.Cost?.SavingRatioMicro is { } ratio) yield return ("savingRatio", PackNumbers.Fraction(ratio));
    }

    /// <summary>
    /// Every certificate a verified pack carries, as this host reads it, trusted or not: one per statement, however many copies
    /// of it travel. Every copy is verified, under its own name, and the statement is trusted when any copy is (review 7, F1:
    /// copies were dropped unverified, so an untrusted respelling that sorted first hid the trusted one, and admission passed
    /// over a newer trusted failure for an older pass). Trust rests on the signature alone: an AEF run beside a certificate is
    /// checked on its own and reported, and one that does not match no longer makes a signed certificate untrusted. So adding
    /// files beside a pack never takes trust away; only removing them can, which §16 states as the distributor's power.
    /// </summary>
    internal static IReadOnlyList<PackEvaluation> Read(VerifiedPack pack, TrustPolicy policy, DateTimeOffset now)
    {
        var evaluations = new List<PackEvaluation>();
        var copies = PackAttestations.Certificates(pack.Attestations)
            .Select(c => (c.Stem, c.Envelope, Identity: PackCertificate.IdentityOf(c.Envelope) ?? "sha256:" + FilesList.Hex(c.Envelope)));
        foreach (var statement in copies.GroupBy(c => c.Identity, StringComparer.Ordinal))
        {
            foreach (var (stem, envelope, identity) in statement)
            {
                var (certificate, signedBy, problems) = PackCertificate.Verify(envelope, pack, policy, now, run: null, stem);
                var run = PackAttestations.Run(pack.Attestations, stem);
                IReadOnlyList<string> runProblems = certificate is not null && run is not null ? PackCertificate.Verify(envelope, pack, policy, now, run, stem).Problems : [];
                var evaluation = new PackEvaluation
                {
                    Digest = identity, File = PackAttestations.CertificatePath(stem), Trusted = certificate is not null, Problems = [.. problems, .. runProblems],
                    EvaluatorKey = signedBy, AefRunIncluded = certificate is not null && run is not null && runProblems.Count == 0,
                };
                // An untrusted certificate is still shown for what it claims, read strictly, never relied on.
                var claimed = certificate ?? Claimed(envelope);
                evaluation = claimed is null ? evaluation : evaluation with
                {
                    Evaluator = claimed.Evaluator, EvaluatedAt = claimed.EvaluatedAt, Tool = $"{claimed.Tool} {claimed.ToolVersion}", Measure = claimed.Measure,
                    Baseline = claimed.Baseline, Confidence = claimed.ConfidencePermille / 1000.0, TaskSource = claimed.TaskSource, Statistic = claimed.Statistic, Passed = claimed.Passed,
                    Checks = [.. claimed.Checks.Select(c => new PackTrialCheck(c.Target, c.RequiredMicro / (double)One, c.ObservedMicro / (double)One, c.Met, c.EstimateMicro / (double)One))],
                    With = Measurement(claimed.With), Without = claimed.Without is { } w ? Measurement(w) : null, AefRunId = claimed.AefRunId, AefRunHash = claimed.AefRunHash,
                };
                evaluations.Add(evaluation);
                // One trusted copy decides its statement (below); the others need not be verified.
                if (evaluation.Trusted) break;
            }
        }
        return Pooled(evaluations);
    }

    /// <summary>
    /// The copies rule, the one place it lives (review 9, C-H1: copies of one pack reached twice kept the first copy's
    /// certificates, so the gate's verdict depended on the order of the arguments, and a pass could hide a trusted failure): one
    /// evaluation per statement, however many copies of it travel, in one pack or in several copies of one pack; trusted when
    /// any copy is; the chosen copy and the order are independent of the order given (by statement, then by file name).
    /// </summary>
    internal static IReadOnlyList<PackEvaluation> Pooled(IEnumerable<PackEvaluation> evaluations) =>
        [.. evaluations.GroupBy(e => e.Digest, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(e => e.Trusted).ThenBy(e => e.File, StringComparer.Ordinal).First())];

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

    private static PackTrialMeasurement Measurement(CertificateArm arm) => new()
    {
        Tasks = arm.Tasks, Hits = arm.Hits, Offered = arm.Offered, Correct = arm.Correct, P95Ms = arm.P95Micros / 1000.0,
        MaximumOffered = arm.MaximumOffered, Spacing = arm.Spacing, PerTask = [.. arm.PerTask.Select(t => new PackTrialTask(t.Offered, t.Correct) { OfferedBytes = t.Bytes })],
    };

    private static CertificateArm Arm(PackTrialMeasurement m) => new(m.Tasks, m.Hits, m.Offered, m.Correct, (long)Math.Round(m.P95Ms * 1000, MidpointRounding.AwayFromZero),
        m.MaximumOffered ?? ModuleTraversal.MaximumOffered, m.Spacing ?? 1, [.. m.PerTask!.Select(t => new CertificateTask(t.Offered, t.Correct, t.OfferedBytes))]);

    // A certificate's numbers are millionths of the target's own unit (feature 02): a rate's are the trial's own integers.
    private const long One = AgentMemory.Packs.Format.PackNumbers.One;

    private static long Micro(double value) => checked((long)Math.Round(value * One, MidpointRounding.AwayFromZero));
}
