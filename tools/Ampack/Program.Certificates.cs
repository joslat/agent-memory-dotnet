using System.Security.Cryptography;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Packs.Format;

namespace Ampack;

/// <summary>
/// Evaluation certificates and the pack card (pack format v1.2 §15–§16): <c>ampack certify</c> (an evaluator signs a
/// certificate from its own trial decision and adds it, with the AEF run, beside a pack), <c>ampack certificate verify</c>
/// (as a host reads one), and <c>ampack admit</c> (a host that trusts certificates for a namespace records a decision from
/// one, signed with its own key). Certificate trust stays a host's choice per namespace; proven here is the default.
/// </summary>
internal static partial class Program
{
    private static int Certify(Options o)
    {
        var target = o.Positional(0, "pack.ampack");
        var archive = PackReader.ReadArchiveFile(target);
        var trust = Trust(o);
        LoadedPack pack;
        try { pack = PackLoader.Load(archive, trust); }
        catch (PackLoadException e) { return Report(e.Problems, ""); }
        // The evaluator's own trial decision for exactly this pack, signed by the key it ran the gate with.
        var records = new PackActivationFolder(o.Required("activations"), Text(o.Required("host-key"), "The host key"));
        var decision = records.Find(pack.Id, AgentMemory.Extensibility.Modules.ModuleVersion.Parse(pack.Version), PackTrialGate.DigestOf(pack.Manifest), pack.Digest);
        if (decision is null)
            return Report([$"No trial decision for exactly this pack ({pack.Id} {pack.Version}, {pack.Digest[..23]}…) signed by that key in {o.Required("activations")}: run the trial first."], "");
        // A certificate travels to others: only from a trial that measured each task as it stood before its change (review 2,
        // 3: a record that does not say, as every record before round 2, may have seen the tests the changes created).
        if (decision.History != "parent")
            return Report([decision.History == "head"
                ? "That decision measured past changes against the repository as it is now (--history head), which sees the tests they created: trial with --history parent before certifying."
                : "That decision does not say how its tasks were drawn (a record from before history was kept): trial again with --history parent before certifying."], "");
        using var signer = Signer(o);
        byte[] certified;
        try
        {
            certified = PackCertificates.Issue(archive, trust, decision, signer, o.Required("evaluator"), o.Required("task-source"), o.Value("aef"),
                o.Value("tool") ?? "agentmemory-pack-gate", o.Value("tool-version") ?? GateVersion());
        }
        catch (PackLoadException e) { return Report(e.Problems, ""); }
        File.WriteAllBytes(o.Required("out"), certified);
        Console.WriteLine($"Certified {pack.Id} {pack.Version} ({(decision.Active ? "passed" : "did not pass")}: {Shown(decision.Reason, 300)})\n  digest  {pack.Digest} (unchanged)"
            + $"\n  by      {Shown(o.Required("evaluator"), 200)} (key {Dsse.Short(signer.KeyId)})" + (o.Value("aef") is { } aef ? $"\n  run     {aef}, beside the certificate" : "")
            + $"\n  wrote   {o.Required("out")}");
        return 0;
    }

    // The version of the library that holds the trial gate (without build metadata): what decided, not this tool's own version.
    private static string GateVersion() =>
        (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(PackTrialGate).Assembly)
            ?.InformationalVersion ?? typeof(PackTrialGate).Assembly.GetName().Version?.ToString(3) ?? "unknown").Split('+')[0];

    private static int CertificateCommand(Options o) => o.Positional(0, "certificate action (verify)") switch
    {
        "verify" => CertificateVerify(o),
        var other => throw new UsageException($"Unknown certificate action '{other}': verify."),
    };

    // Verifies the pack, then one certificate (inside it under META/attestations/, or a file) or every one it carries.
    private static int CertificateVerify(Options o)
    {
        var target = o.Positional(1, "pack.ampack");
        var content = PackReader.Read(target);
        var policy = Policy(o);
        // A set verifies whole, member by member, as the Python verifier and receipt verify do (review 1, H3).
        var verification = PackVerifier.VerifyWhole(content, policy, DateTimeOffset.UtcNow);
        if (verification.Pack is not { } pack) return Report(verification.Problems, "");
        var chosen = o.Value("certificate");
        var certificates = PackAttestations.Certificates(pack.Attestations).Where(c => chosen is null || PackAttestations.CertificatePath(c.Stem) == chosen)
            .Select(c => (Name: PackAttestations.CertificatePath(c.Stem), c.Envelope, Run: PackAttestations.Run(pack.Attestations, c.Stem), Stem: (string?)c.Stem)).ToList();
        if (chosen is not null && certificates.Count == 0)
        {
            if (chosen.StartsWith(PackPaths.AttestationsFolder, StringComparison.Ordinal)) return Report([$"The pack carries no {chosen}."], "");
            certificates.Add((chosen, PackReader.ReadFile(chosen, Dsse.MaximumEnvelopeBytes, "The certificate"), null, null));
        }
        if (certificates.Count == 0) return Report([$"{pack.Id} {pack.Version} carries no evaluation certificate."], "");
        var problems = new List<string>();
        // One verdict per statement, however many copies of it travel: it holds when any copy verifies (the copies rule,
        // PackCertificates.Pooled; review 9, D3: each copy was judged alone, so a broken copy added beside a trusted certificate,
        // outside the signed list, turned this command's answer into a refusal).
        foreach (var statement in certificates.GroupBy(c => PackCertificate.IdentityOf(c.Envelope) ?? c.Name, StringComparer.Ordinal))
        {
            var refusals = new List<string>();
            var verified = false;
            foreach (var (name, envelope, run, stem) in statement.OrderBy(c => c.Name, StringComparer.Ordinal))
            {
            var (certificate, signedBy, refused) = PackCertificate.Verify(envelope, pack, policy, DateTimeOffset.UtcNow, run, stem);
            if (certificate is null) { refusals.AddRange(refused.Select(p => $"{name}: {p}")); continue; }
            verified = true;
            Console.WriteLine($"Verified the certificate {name}\n  pack    {pack.Id} {pack.Version} {pack.Digest}\n  by      {Shown(certificate.Evaluator, 200)} (key {Dsse.Short(signedBy!)}), {certificate.EvaluatedAt:u}"
                + $"\n  tool    {Shown(certificate.Tool)} {Shown(certificate.ToolVersion)}\n  trial   {certificate.Measure} against {certificate.Baseline}{(certificate.ConfidenceMilli is { } c ? $", confidence {c / 1000.0:0.###}" : "")}, {certificate.With.Tasks} tasks from {Shown(certificate.TaskSource, 500)}"
                + $"\n  result  {(certificate.Passed ? "passed" : "did not pass")}: " + string.Join(", ", certificate.Checks.Select(k => $"{k.Target} {k.ObservedMilli / 1000.0:0.###} ({(k.Met ? "met" : "MISSED")} {k.RequiredMilli / 1000.0:0.###})"))
                + (certificate.AefRunId is { } runId ? $"\n  aef     {runId} {certificate.AefRunHash![..16]}… {(run is null ? "(the run travels elsewhere)" : "(the run travels beside it and matches)")}" : ""));
            break;
            }
            if (!verified) problems.AddRange(refusals);
        }
        return Report(problems, "");
    }

    // A host that trusts certificates for this pack's namespace records a decision from the latest trusted one that covers the
    // declared trial, signed with its own key; ModuleSetBuilder.TrustCertificatesFor decides where such a decision counts.
    private static int Admit(Options o)
    {
        var target = o.Positional(0, "pack.ampack");
        LoadedPack pack;
        try { pack = PackLoader.Load(PackReader.ReadArchiveFile(target), Trust(o)); }
        catch (PackLoadException e) { return Report(e.Problems, ""); }
        var (decision, reason) = PackCertificates.Admit(pack, DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        if (decision is null) return Report([reason], "");
        using var hostKey = ECDsa.Create();
        hostKey.ImportFromPem(PrivatePem(o.Required("host-signing-key")));
        // This host's own evidence comes first: where it trialled exactly this pack, that decides, pass or fail, and a decision
        // from a certificate would never count (review 2).
        var own = new PackActivationFolder(o.Required("activations"), hostKey.ExportSubjectPublicKeyInfoPem()).History(pack.Manifest.Id)
            .Where(a => a.Basis is null && a.Version == decision.Version && a.ManifestDigest == decision.ManifestDigest && a.PackDigest == decision.PackDigest)
            .MaxBy(a => a.DecidedAt);
        if (own is not null)
            return Report([$"This host's own trial of exactly this pack {(own.Active ? "passed" : "failed")} on {own.DecidedAt:u} ({Shown(own.Reason, 300)}): its own evidence decides, and a certificate does not overrule it."], "");
        var file = PackActivationFolder.Write(o.Required("activations"), decision, hostKey);
        Console.WriteLine($"Admitted {pack.Id} {pack.Version}: {Shown(decision.Reason, 300)}\n  basis   certificate {decision.Basis!.Certificate[..23]}… by {Shown(decision.Basis.Evaluator, 200)} (key {Dsse.Short(decision.Basis.EvaluatorKey)})"
            + $"\n  signed  {file}\n  A host composes it only where it trusts certificates for its namespace (ModuleSetBuilder.TrustCertificatesFor); elsewhere it needs its own trial.");
        return 0;
    }

    // What this host's gate does with packs, from its signed decisions alone: on or off, and why; no store, no trial run.
    private static int Gate(Options o)
    {
        var trust = Trust(o);
        var packs = new List<LoadedPack>();
        foreach (var path in o.PositionalFrom(0))
        {
            try { packs.Add(PackLoader.Load(PackReader.ReadArchiveFile(path), trust)); }
            catch (PackLoadException e) { return Report(e.Problems.Select(p => $"{path}: {p}"), ""); }
        }
        if (packs.Count == 0) throw new UsageException("gate needs at least one pack.");
        var activations = new PackActivationFolder(o.Required("activations"), Text(o.Required("host-key"), "The host key"));
        var check = GateCheck(o, packs, activations);
        foreach (var id in check.On)
        {
            var pack = packs.First(p => p.Id == id);
            Console.WriteLine($"  on   {id} {pack.Version}" + (pack.Manifest.Trial is null ? "  (declares no trial)"
                : check.OnCertificate.TryGetValue(id, out var basis) ? $"  on {Shown(basis.Evaluator, 200)}'s certificate, trusted here for its namespace" : "  on this host's own trial"));
        }
        foreach (var off in check.Off) Console.WriteLine($"  off  {off.ModuleId} {off.Version}  {Shown(off.Reason, 300)}");
        if (activations.Ignored.Count > 0) Console.WriteLine($"  ({activations.Ignored.Count} file(s) in {o.Required("activations")} are not records signed by this host and were ignored)");
        return 0;
    }

    // The card and the certificates of a verified pack, as verify shows them.
    private static void ShowCardAndCertificates(VerifiedPack pack, TrustPolicy policy)
    {
        if (pack.Files.TryGetValue(PackCard.FileName, out var cardBytes) && PackCard.Parse(cardBytes).Card is { } card)
            Console.WriteLine($"  card    {Shown(card.OwnerName, 200)}: {Shown(card.IntendedUse, 200)}\n          not for: {Shown(card.Limitations, 200)}"
                + (card.Boundaries.Count > 0 ? $"\n          boundaries: {Shown(string.Join("; ", card.Boundaries), 300)}" : ""));
        foreach (var (stem, envelope) in PackAttestations.Certificates(pack.Attestations))
        {
            var (certificate, signedBy, problems) = PackCertificate.Verify(envelope, pack, policy, DateTimeOffset.UtcNow, PackAttestations.Run(pack.Attestations, stem), stem);
            Console.WriteLine(certificate is not null
                ? $"  certificate {(certificate.Passed ? "passed" : "did not pass")}, by {Shown(certificate.Evaluator, 200)} (key {Dsse.Short(signedBy!)}) on {certificate.EvaluatedAt:yyyy-MM-dd}, {certificate.With.Tasks} tasks, trusted here"
                : $"  certificate {stem}: not trusted here: {Shown(problems[0], 300)}");
        }
    }

    // The options of the host's trial gate, the same at every tool door that applies it (review 8, G3).
    private static readonly string[] GateOptions =
        ["activations", "host-key", "require-trials-for", "trust-certificates-for", "floor-confidence", "floor-tasks", "floor-targets"];

    // The host's trial gate as every tool door applies it (review 8, G3: gate and serve each read their own options, and neither
    // read a floor, so both showed and served packs a host with a floor keeps off): where trials are required, where
    // certificates count, and the least a trial must show (ModuleSetBuilder.RequireTrialFloor). An empty pack list checks the
    // options alone.
    private static PackGateCheck GateCheck(Options o, IReadOnlyList<LoadedPack> packs, IPackActivations activations, IReadOnlySet<string>? familyMembers = null)
    {
        static string[] Prefixes(string? value) => value is null ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        try { return PackGateCheck.Of(packs, activations, Prefixes(o.Value("require-trials-for")), Prefixes(o.Value("trust-certificates-for")), familyMembers, Floor(o)); }
        catch (ArgumentException e) { throw new UsageException(e.Message); }
    }

    // --floor-confidence, --floor-tasks and --floor-targets (name=value, comma-separated): the host's floor, or null without one.
    private static PackTrialFloor? Floor(Options o)
    {
        var (confidence, tasks, targets) = (o.Value("floor-confidence"), o.Value("floor-tasks"), o.Value("floor-targets"));
        if (confidence is null && tasks is null && targets is null) return null;
        double? least = null;
        if (confidence is not null)
            least = double.TryParse(confidence, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var c) && c is 0.9 or 0.95 or 0.975 or 0.99
                ? c : throw new UsageException("--floor-confidence is 0.9, 0.95, 0.975 or 0.99.");
        var fewest = ModuleTrial.MinimumTasks;
        if (tasks is not null)
            fewest = int.TryParse(tasks, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n >= ModuleTrial.MinimumTasks && n <= 100_000
                ? n : throw new UsageException($"--floor-tasks is a whole number from {ModuleTrial.MinimumTasks} to 100000.");
        ModuleTrialTargets? goals = null;
        if (targets is not null)
        {
            goals = new ModuleTrialTargets();
            foreach (var pair in targets.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = pair.Split('=', 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2 || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                    throw new UsageException($"--floor-targets takes name=value pairs; '{Shown(pair, 80)}' is not one.");
                goals = parts[0] switch
                {
                    "hitRate" => goals with { HitRate = value },
                    "precision" => goals with { Precision = value },
                    "lift" => goals with { Lift = value },
                    "precisionLift" => goals with { PrecisionLift = value },
                    "addedP95Ms" => goals with { AddedP95Ms = value },
                    _ => throw new UsageException($"--floor-targets names hitRate, precision, lift, precisionLift or addedP95Ms, not '{Shown(parts[0], 80)}'."),
                };
            }
        }
        return new PackTrialFloor { Confidence = least, Tasks = fewest, Targets = goals };
    }
}
