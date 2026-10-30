using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentMemory.Extensibility;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// DKP-2 and DKP-1 (pack format v1.2 §16): an evaluator signs a certificate of its own trial and it travels beside the pack,
/// outside the files list, so the digest does not change; a host reads it, trusts it only from a key it names as evaluator for
/// the pack's id, and admits a pack on it only where it chose certificate trust. Proven here stays the default.
/// </summary>
public sealed class PackCertificateTests : IDisposable
{
    private const string Id = "io.agentmemory.packs.martialarts";
    private const string Trial = """{ "measure": "test-impact", "tasks": 40, "targets": { "hitRate": 0.5 } }""";
    private static readonly string MartialArts = Path.Combine(AppContext.BaseDirectory, "Packs", "MartialArts");
    private readonly PackSigningKey _publisher = PackAuthoring.CreateKey();
    private readonly PackSigningKey _evaluator = PackAuthoring.CreateKey();
    private readonly string _folder = Directory.CreateTempSubdirectory("pack-certificate-").FullName;
    private readonly ECDsa _host = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _host.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void A_certificate_travels_beside_the_pack_without_changing_its_digest_and_a_host_reads_it()
    {
        var (built, pack) = Built();
        var certified = Certify(built, pack);

        var loaded = PackLoader.Load(certified, Trust());

        loaded.Digest.Should().Be(pack.Digest, "the certificate is outside the files list");
        var certificate = loaded.Certificates.Should().ContainSingle().Subject;
        certificate.Trusted.Should().BeTrue();
        certificate.Problems.Should().BeEmpty();
        certificate.Evaluator.Should().Be("Example Lab");
        certificate.EvaluatorKey.Should().Be(_evaluator.KeyId);
        certificate.Passed.Should().BeTrue();
        certificate.Measure.Should().Be("test-impact");
        certificate.Baseline.Should().Be("none");
        certificate.TaskSource.Should().Be("40 changes from the lab's repository");
        certificate.Checks.Should().ContainSingle().Which.Should().Be(new PackTrialCheck("hitRate", 0.5, 0.6, true));
        certificate.With!.Tasks.Should().Be(40);
        certificate.With.P95Ms.Should().Be(12.5);
        certificate.File.Should().StartWith("META/attestations/" + _evaluator.KeyId[..16] + "-").And.EndWith(".certificate.dsse.json");
        PackInspection.Inspect(certified, Trust()).Certificates.Should().ContainSingle().Which.Trusted.Should().BeTrue();

        // A countersignature after certification keeps both: the content, and so the certificate's subject, is unchanged.
        var curator = PackAuthoring.CreateKey();
        PackLoader.Load(PackAuthoring.Countersign(certified, curator.PrivateKeyPem, "curator"), Trust()).Certificates.Should().ContainSingle().Which.Trusted.Should().BeTrue();
    }

    [Fact]
    public void A_certificate_from_a_key_the_host_does_not_name_as_evaluator_informs_but_admits_nothing()
    {
        var (built, pack) = Built();
        var certified = Certify(built, pack);

        foreach (var trust in new[] { Trust(evaluator: false), Trust(evaluatorRole: "assurer"), Trust(evaluatorPrefix: "io.other") })
        {
            var loaded = PackLoader.Load(certified, trust);
            var certificate = loaded.Certificates.Should().ContainSingle().Subject;
            certificate.Trusted.Should().BeFalse();
            certificate.Problems.Should().NotBeEmpty();
            certificate.Evaluator.Should().Be("Example Lab", "what it claims is shown, read strictly, never relied on");
            certificate.EvaluatorKey.Should().BeNull();
            PackCertificates.Admit(loaded, DateTimeOffset.UtcNow).Decision.Should().BeNull();
            PackAdvice.Of(loaded, DateTimeOffset.UtcNow).Select(a => a.Rule).Should().Contain("certificate.untrusted");
        }
    }

    [Fact]
    public void Only_a_trial_of_exactly_this_pack_can_be_certified()
    {
        var (built, pack) = Built();
        var decision = Decision(pack);

        Refused(() => Certify(built, pack, decision with { PackDigest = "sha256:" + new string('a', 64) })).Should().Contain(p => p.Contains("other content"));
        Refused(() => Certify(built, pack, decision with { ManifestDigest = "jcs-sha256:" + new string('a', 64) })).Should().Contain(p => p.Contains("other declarations"));
        Refused(() => Certify(built, pack, decision with { Version = "9.9.9" })).Should().Contain(p => p.Contains("9.9.9"));
        Refused(() => Certify(built, pack, decision with { With = null })).Should().Contain(p => p.Contains("no measurement"));
        Refused(() => Certify(built, pack, decision with { Basis = Basis() })).Should().Contain(p => p.Contains("only a trial can be certified"));
        var (untrialled, plain) = Built(trial: null);
        Refused(() => Certify(untrialled, plain, decision)).Should().Contain(p => p.Contains("declares no trial"));
    }

    [Fact]
    public void An_honest_failure_is_certified_as_one_and_admits_nothing()
    {
        var (built, pack) = Built();
        var failed = PackTrialGate.Decide(pack.Manifest, Arm(16), Arm(0), Earlier, pack.Digest);
        failed.Active.Should().BeFalse();

        var loaded = PackLoader.Load(Certify(built, pack, failed), Trust());

        loaded.Certificates.Single().Passed.Should().BeFalse();
        loaded.Certificates.Single().Trusted.Should().BeTrue("a signed, honest failure is a valid certificate");
        PackCertificates.Admit(loaded, DateTimeOffset.UtcNow).Reason.Should().Contain("it did not pass");
    }

    [Fact]
    public void A_host_admits_on_a_trusted_certificate_that_covers_the_declared_trial_and_signs_the_decision_itself()
    {
        var (built, pack) = Built();
        var loaded = PackLoader.Load(Certify(built, pack), Trust());

        var (decision, _) = PackCertificates.Admit(loaded, Now);
        var file = PackActivationFolder.Write(_folder, decision!, _host);

        decision!.Active.Should().BeTrue();
        decision.PackDigest.Should().Be(pack.Digest);
        decision.ManifestDigest.Should().Be(PackTrialGate.DigestOf(pack.Manifest));
        decision.Basis!.Kind.Should().Be(PackCertificates.CertificateBasis);
        decision.Basis.Certificate.Should().Be(loaded.Certificates.Single().Digest);
        decision.Basis.EvaluatorKey.Should().Be(_evaluator.KeyId);
        decision.Basis.TaskSource.Should().Be("40 changes from the lab's repository");
        Path.GetFileName(file).Should().EndWith("-certificate.dsse.json");
        var read = Activations().Find(Id, pack.Manifest.Version, decision.ManifestDigest, pack.Digest);
        read!.Basis.Should().Be(decision.Basis, "the basis is signed into the host's record and read back");
        PackAdvice.Of(loaded, DateTimeOffset.UtcNow).Select(a => a.Rule).Should().Contain("certificate.admits");
    }

    [Theory]
    [InlineData("retrieval", "none", null, "hitRate", 500L, "it measured retrieval")]
    [InlineData("test-impact", "host", null, "hitRate", 500L, "it compared with host")]
    [InlineData("test-impact", "none", 950, "hitRate", 500L, "confidence")]
    [InlineData("test-impact", "none", null, "hitRate", 400L, "not the declared 0.5")]
    [InlineData("test-impact", "none", null, "precision", 500L, "does not check hitRate")]
    public void A_certificate_admits_only_the_trial_the_pack_declares(string measure, string baseline, int? confidence, string target, long required, string gap)
    {
        // Issued through PackCertificates a certificate states the declared trial (the digest covers evals/trial.json); one
        // signed by other tooling may state another, valid in itself and trusted, and then it does not admit the pack.
        var (built, pack) = Built();
        using var signer = PackSigner.FromPem(_evaluator.PrivateKeyPem);
        var certificate = new PackCertificate(Id, pack.Version, pack.Digest, "Other Lab", "other-tool", "1.0", Earlier, measure, baseline, confidence,
            "its own tasks", PackTrialGate.Statistic, [new CertificateCheck(target, required, 600, true, null)], ArmOf(40, 24, 120, 30, 12500),
            ArmOf(40, 0, 0, 0, 2500, cap: baseline == "host" ? ModuleTraversal.MaximumOffered : 0), true, null, null);
        var files = new Dictionary<string, byte[]>(PackReader.ReadArchive(built, "built").Files, StringComparer.Ordinal)
        {
            [PackAttestations.CertificatePath(PackAttestations.StemFor(signer.KeyId, Earlier))] = certificate.Sign(signer),
        };

        var loaded = PackLoader.Load(StrictZip.Write(files, compress: true), Trust());

        loaded.Certificates.Single().Trusted.Should().BeTrue();
        var (decision, reason) = PackCertificates.Admit(loaded, Now);
        decision.Should().BeNull();
        reason.Should().Contain(gap);
    }

    [Fact]
    public void A_certificate_moved_onto_other_content_is_not_trusted_there()
    {
        var (built, pack) = Built();
        var certificate = PackReader.ReadArchive(Certify(built, pack), "certified").Files.Single(f => f.Key.StartsWith("META/attestations/", StringComparison.Ordinal));
        var (other, _) = Built("""{ "measure": "test-impact", "tasks": 40, "targets": { "hitRate": 0.4 } }""");
        var moved = new Dictionary<string, byte[]>(PackReader.ReadArchive(other, "other").Files, StringComparer.Ordinal) { [certificate.Key] = certificate.Value };

        var loaded = PackLoader.Load(StrictZip.Write(moved, compress: true), Trust());

        loaded.Certificates.Single().Trusted.Should().BeFalse();
        loaded.Certificates.Single().Problems.Should().Contain(p => p.Contains("not this pack"));
        Refused(() => Certify(other, loaded, Decision(pack) with { ManifestDigest = PackTrialGate.DigestOf(loaded.Manifest), PackDigest = loaded.Digest }))
            .Should().Contain(p => p.Contains("does not check hitRate at the declared 0.4"), "a certificate made from another trial's decision would misstate it");
    }

    [Fact]
    public void A_decision_made_from_a_certificate_counts_only_where_the_host_trusts_certificates()
    {
        var (built, pack) = Built();
        var loaded = PackLoader.Load(Certify(built, pack), Trust());
        PackActivationFolder.Write(_folder, PackCertificates.Admit(loaded, Now).Decision!, _host);

        Gated(m => m.AddPack(loaded)).SwitchedOff.Should().ContainSingle().Which.Reason.Should().Contain("Example Lab's certificate")
            .And.Contain("needs its own trial", "proven here is the default");
        Gated(m => m.AddPack(loaded).TrustCertificatesFor("io.other")).SwitchedOff.Should().ContainSingle();
        Gate(loaded, "io.agentmemory.packs").On.Should().Equal(Id);
        Gate(loaded, "io.agentmemory.packs.martialartsx").Off.Should().ContainSingle("a namespace covers whole segments");

        // Outside certificate trust the host's own passing trial of exactly this content decides, even if it is older.
        PackActivationFolder.Write(_folder, PackTrialGate.Decide(pack.Manifest, Arm(30), Arm(0), Earlier, pack.Digest), _host);
        Gate(loaded).On.Should().Equal(Id);
        // And a later own trial that fails switches it off even where certificates are trusted: the latest decision decides.
        PackActivationFolder.Write(_folder, PackTrialGate.Decide(pack.Manifest, Arm(2), Arm(0), Now.AddMinutes(1), pack.Digest), _host);
        Gate(loaded, "io.agentmemory").Off.Should().ContainSingle().Which.Reason.Should().StartWith("its trial failed");
    }

    [Fact]
    public void A_certificate_does_not_skip_the_upgrade_gate()
    {
        // 1.0.0 passed here on the host's own trial; 1.0.1 declares no regression and arrives with a certificate. The evaluator
        // could not compare against this host's 1.0.0, so the upgrade still needs this host's own trial.
        const string rule = """{ "measure": "test-impact", "tasks": 40, "targets": { "hitRate": 0.5 }, "upgrade": { "noRegression": true } }""";
        var (_, first) = Built(rule);
        PackActivationFolder.Write(_folder, PackTrialGate.Decide(first.Manifest, Arm(30), Arm(0), Earlier, first.Digest), _host);
        var (built, next) = Built(rule, version: "9.9.9");
        var loaded = PackLoader.Load(Certify(built, next), Trust());
        PackActivationFolder.Write(_folder, PackCertificates.Admit(loaded, Now).Decision!, _host);

        Gate(loaded, "io.agentmemory").Off.Should().ContainSingle().Which.Reason.Should().Contain("must compare against that version");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("1")]
    [InlineData("\"x\"")]
    [InlineData("{\"payload\":\"\\ud800\"}")]
    [InlineData("{\"payload\":\"not base64!\"}")]
    [InlineData("not json")]
    public void An_unsigned_attestation_anyone_can_add_never_stops_the_pack_from_loading(string junk)
    {
        // Review 1, H1: attestations are outside what is signed; what one claims is read without ever throwing.
        var (built, pack) = Built();
        var files = new Dictionary<string, byte[]>(PackReader.ReadArchive(built, "built").Files, StringComparer.Ordinal)
        {
            ["META/attestations/abc.certificate.dsse.json"] = Encoding.UTF8.GetBytes(junk),
        };
        var archive = StrictZip.Write(files, compress: true);

        var loaded = PackLoader.Load(archive, Trust());

        loaded.Digest.Should().Be(pack.Digest);
        var certificate = loaded.Certificates.Should().ContainSingle().Subject;
        certificate.Trusted.Should().BeFalse();
        certificate.Evaluator.Should().BeNull();
        PackInspection.Inspect(archive, Trust()).Loaded.Should().BeTrue();
        PackCertificates.Admit(loaded, Now).Decision.Should().BeNull();
        PackAdvice.Of(loaded, Now).Select(a => a.Rule).Should().Contain("certificate.untrusted");
    }

    [Fact]
    public void An_evaluator_key_this_host_revoked_before_is_not_trusted_even_when_this_run_names_no_revocation()
    {
        // Review 1, H2: the host's ledger keeps what it revoked; certificates are read under the same trust as the pack.
        var (built, pack) = Built();
        var certified = Certify(built, pack);
        var path = Path.Combine(_folder, "ledger.json");
        var kept = HostLedger.Load(path);
        kept.Record([], [new RevokedKey(_evaluator.KeyId, null)]);
        kept.Save(path);
        var ledger = PackInstallLedger.Open(path);

        var loaded = PackLoader.Load(certified, Trust(), ledger);

        loaded.Certificates.Single().Trusted.Should().BeFalse();
        loaded.Certificates.Single().Problems.Should().Contain(p => p.Contains("revoked"));
        PackCertificates.Admit(loaded, Now).Decision.Should().BeNull();
    }

    [Fact]
    public void A_certificate_decision_counts_only_while_its_certificate_travels_with_the_pack_and_is_trusted_now()
    {
        // Review 1, M1: admitted on the evaluator's certificate; then the host stops trusting the evaluator, or the certificate
        // is stripped from the pack. The record the host signed earlier no longer switches it on.
        var (built, pack) = Built();
        var certified = Certify(built, pack);
        PackActivationFolder.Write(_folder, PackCertificates.Admit(PackLoader.Load(certified, Trust()), Now).Decision!, _host);

        Gate(PackLoader.Load(certified, Trust()), "io.agentmemory").On.Should().Equal(Id);
        Gate(PackLoader.Load(certified, Trust(evaluator: false)), "io.agentmemory").Off.Should().ContainSingle()
            .Which.Reason.Should().Contain("or is no longer trusted here");
        Gate(PackLoader.Load(built, Trust()), "io.agentmemory").Off.Should().ContainSingle()
            .Which.Reason.Should().Contain("no longer travels with it");
        // The host's own passing trial still switches it on when the certificate is gone.
        PackActivationFolder.Write(_folder, PackTrialGate.Decide(pack.Manifest, Arm(30), Arm(0), Earlier, pack.Digest), _host);
        Gate(PackLoader.Load(built, Trust()), "io.agentmemory").On.Should().Equal(Id);
    }

    [Fact]
    public void A_certificate_never_overrules_the_hosts_own_failed_trial_of_exactly_this_pack()
    {
        var (built, pack) = Built();
        var loaded = PackLoader.Load(Certify(built, pack), Trust());
        PackActivationFolder.Write(_folder, PackTrialGate.Decide(pack.Manifest, Arm(2), Arm(0), Earlier, pack.Digest), _host);
        PackActivationFolder.Write(_folder, PackCertificates.Admit(loaded, Now).Decision!, _host);

        Gate(loaded, "io.agentmemory").Off.Should().ContainSingle().Which.Reason.Should().StartWith("its trial failed");
    }

    [Fact]
    public void Outside_certificate_trust_a_certificate_decision_is_never_the_version_that_passed_here()
    {
        // Review 1, M2: 1.0.0 passed on this host's own trial; 1.1.0 was admitted on a certificate where certificates are not
        // trusted (it stays off); 1.2.0 declares no regression. It must compare against 1.0.0, not against 1.1.0.
        const string rule = """{ "measure": "test-impact", "tasks": 40, "targets": { "hitRate": 0.5 }, "upgrade": { "noRegression": true } }""";
        var (_, first) = Built(rule, version: "1.0.0");
        PackActivationFolder.Write(_folder, PackTrialGate.Decide(first.Manifest, Arm(36), Arm(0), Earlier.AddMinutes(-10), first.Digest), _host);
        var (middleBuilt, middle) = Built(rule, version: "1.1.0");
        PackActivationFolder.Write(_folder, PackCertificates.Admit(PackLoader.Load(Certify(middleBuilt, middle), Trust()), Earlier.AddMinutes(-5)).Decision!, _host);
        var (_, last) = Built(rule, version: "1.2.0");
        var previous = new PackTrialPrevious { Version = "1.1.0", ManifestDigest = PackTrialGate.DigestOf(middle.Manifest), PackDigest = middle.Digest, With = Arm(24) };
        PackActivationFolder.Write(_folder, PackTrialGate.Decide(last.Manifest, Arm(24), Arm(0), Earlier, last.Digest, previous), _host);

        Gate(last).Off.Should().ContainSingle().Which.Reason.Should().Contain("1.0.0");
    }

    [Fact]
    public void Where_the_host_trialled_the_pack_itself_its_own_decision_decides_and_a_later_admission_changes_nothing()
    {
        // Review 2, M1: 1.0.0 and 1.1.0 passed this host's own trials (1.1.0 compared against 1.0.0 on the same tasks). An
        // admission of 1.1.0 on a certificate afterwards must not hide its own pass and switch the upgrade off.
        const string rule = """{ "measure": "test-impact", "tasks": 40, "targets": { "hitRate": 0.5 }, "upgrade": { "noRegression": true } }""";
        var (_, first) = Built(rule, version: "1.0.0");
        PackActivationFolder.Write(_folder, PackTrialGate.Decide(first.Manifest, Arm(30), Arm(0), Earlier.AddMinutes(-10), first.Digest), _host);
        var (built, next) = Built(rule, version: "1.1.0");
        var previous = new PackTrialPrevious { Version = "1.0.0", ManifestDigest = PackTrialGate.DigestOf(first.Manifest), PackDigest = first.Digest, With = Arm(30) };
        PackActivationFolder.Write(_folder, PackTrialGate.Decide(next.Manifest, Arm(30), Arm(0), Earlier, next.Digest, previous), _host);
        var loaded = PackLoader.Load(Certify(built, next), Trust());
        PackActivationFolder.Write(_folder, PackCertificates.Admit(loaded, Now).Decision!, _host);

        var gate = PackGateCheck.Of([loaded], Activations(), trustCertificatesFor: ["io.agentmemory"]);

        gate.On.Should().Equal(Id);
        gate.OnCertificate.Should().BeEmpty("its own trial switched it on");
    }

    [Fact]
    public void A_version_whose_own_trial_failed_is_never_the_version_that_passed_here_even_if_later_admitted_on_a_certificate()
    {
        // Review 2, M2: the upgrade gate resolves each version's decisions by the same rule as the gate itself.
        const string rule = """{ "measure": "test-impact", "tasks": 40, "targets": { "hitRate": 0.5 }, "upgrade": { "noRegression": true } }""";
        var (firstBuilt, first) = Built(rule, version: "1.0.0");
        PackActivationFolder.Write(_folder, PackTrialGate.Decide(first.Manifest, Arm(2), Arm(0), Earlier.AddMinutes(-10), first.Digest), _host);
        PackActivationFolder.Write(_folder, PackCertificates.Admit(PackLoader.Load(Certify(firstBuilt, first), Trust()), Earlier.AddMinutes(-5)).Decision!, _host);
        var (_, next) = Built(rule, version: "1.1.0");
        PackActivationFolder.Write(_folder, PackTrialGate.Decide(next.Manifest, Arm(30), Arm(0), Earlier, next.Digest), _host);

        Gate(next, "io.agentmemory").On.Should().Equal(new[] { Id }, "nothing passed here before it, so there is nothing to compare against");
    }

    [Fact]
    public void An_admission_counts_only_while_its_certificate_is_the_latest_trusted_one_the_pack_carries()
    {
        // Review 2, L3: a newer trusted certificate beside the one an admission named withdraws that admission until the host
        // admits again (or trials the pack itself).
        var (built, pack) = Built();
        var older = Certify(built, pack, PackTrialGate.Decide(pack.Manifest, Arm(24), Arm(0), Earlier.AddDays(-30), pack.Digest));
        PackActivationFolder.Write(_folder, PackCertificates.Admit(PackLoader.Load(older, Trust()), Earlier).Decision!, _host);
        var both = PackLoader.Load(Certify(older, pack, PackTrialGate.Decide(pack.Manifest, Arm(4), Arm(0), Earlier.AddDays(-1), pack.Digest)), Trust());

        Gate(PackLoader.Load(older, Trust()), "io.agentmemory").On.Should().Equal(Id);
        Gate(both, "io.agentmemory").Off.Should().ContainSingle().Which.Reason.Should().Contain("no longer the latest trusted certificate");
    }

    [Fact]
    public void A_pack_composed_as_a_family_member_or_as_code_relies_on_the_hosts_own_trials_only()
    {
        // Review 2, L5: said plainly, not as if the certificate had gone.
        var (built, pack) = Built();
        PackActivationFolder.Write(_folder, PackCertificates.Admit(PackLoader.Load(Certify(built, pack), Trust()), Now).Decision!, _host);
        var builder = new ModuleSetBuilder().AddDefinitionPack(pack.Manifest, pack.Digest, "publisher");

        var (on, off) = PackTrialGate.Apply(builder.Modules, Activations(), new PackTrialPolicy([], ["io.agentmemory"], null));

        on.Should().BeEmpty();
        off.Should().ContainSingle().Which.Reason.Should().Contain("composed as part of a family or as compiled code");
    }

    [Fact]
    public void The_latest_trusted_certificate_decides_and_issuing_again_replaces_the_earlier_one()
    {
        // Review 1, L1 and L3: a newer failure is not passed over for an older pass; the same stem certified again replaces
        // the certificate and the run beside it.
        var (built, pack) = Built();
        var older = Certify(built, pack, PackTrialGate.Decide(pack.Manifest, Arm(24), Arm(0), Earlier.AddDays(-30), pack.Digest));
        var both = Certify(older, pack, PackTrialGate.Decide(pack.Manifest, Arm(4), Arm(0), Earlier, pack.Digest));

        var loaded = PackLoader.Load(both, Trust());
        loaded.Certificates.Should().HaveCount(2).And.OnlyContain(c => c.Trusted);
        PackCertificates.Admit(loaded, Now).Reason.Should().Contain("did not pass");

        var run = Directory.CreateDirectory(Path.Combine(_folder, "run")).FullName;
        File.WriteAllText(Path.Combine(run, "run.json"), """{"runId":"r1"}""" + "\n");
        File.WriteAllText(Path.Combine(run, "extra.ndjson"), "{}\n");
        var withRun = PackCertificates.Issue(built, Trust(), Decision(pack), _evaluator.PrivateKeyPem, "Example Lab", "x", run);
        var again = PackCertificates.Issue(withRun, Trust(), Decision(pack), _evaluator.PrivateKeyPem, "Example Lab", "x");
        var replaced = PackLoader.Load(again, Trust());
        replaced.Certificates.Should().ContainSingle().Which.Trusted.Should().BeTrue("the old run went with the old certificate");
        PackReader.ReadArchive(again, "again").Files.Keys.Should().NotContain(k => k.Contains(".aef/", StringComparison.Ordinal));
    }

    [Fact]
    public void Certificate_trust_needs_the_hosts_decisions_and_a_namespace()
    {
        FluentActions.Invoking(() => Compose(m => m.TrustCertificatesFor("io.example"))).Should().Throw<ModuleActivationException>();
        FluentActions.Invoking(() => new ServiceCollection().AddAgentMemoryModules(m => m.TrustCertificatesFor("io..example"))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_aef_run_travels_beside_its_certificate_named_by_its_run_hash()
    {
        var run = Directory.CreateDirectory(Path.Combine(_folder, "run")).FullName;
        File.WriteAllText(Path.Combine(run, "run.json"), """{"schemaVersion":"1.0","runId":"trial.io.agentmemory.packs.martialarts.1"}""" + "\n");
        Directory.CreateDirectory(Path.Combine(run, "results"));
        File.WriteAllText(Path.Combine(run, "results", "with-pack.ndjson"), """{"task":1,"hit":true}""" + "\n");
        File.WriteAllText(Path.Combine(run, "seal.json"), "{}\n");
        var (built, pack) = Built();

        var certified = PackCertificates.Issue(built, Trust(), Decision(pack), _evaluator.PrivateKeyPem, "Example Lab", "40 changes from the lab's repository", run);
        var loaded = PackLoader.Load(certified, Trust());

        var certificate = loaded.Certificates.Single();
        certificate.Trusted.Should().BeTrue();
        certificate.AefRunIncluded.Should().BeTrue();
        certificate.AefRunId.Should().Be("trial.io.agentmemory.packs.martialarts.1");
        certificate.AefRunHash.Should().MatchRegex("^[0-9a-f]{64}$");
        // A changed result under the same certificate no longer matches the run hash it names.
        var files = new Dictionary<string, byte[]>(PackReader.ReadArchive(certified, "certified").Files, StringComparer.Ordinal);
        var result = files.Keys.Single(k => k.EndsWith("results/with-pack.ndjson", StringComparison.Ordinal));
        files[result] = Encoding.UTF8.GetBytes("""{"task":1,"hit":false}""" + "\n");
        var tampered = PackLoader.Load(StrictZip.Write(files, compress: true), Trust()).Certificates.Single();
        // Trust rests on the signature alone, so adding or changing files beside a pack never takes it away (review 7, F1); the
        // run is reported as not matching, and no longer counts as included.
        tampered.Trusted.Should().BeTrue();
        tampered.AefRunIncluded.Should().BeFalse();
        tampered.Problems.Should().Contain(p => p.Contains("run hash"));
        // A folder that is not an AEF run is refused before anything is signed.
        File.Delete(Path.Combine(run, "run.json"));
        Refused(() => PackCertificates.Issue(built, Trust(), Decision(pack), _evaluator.PrivateKeyPem, "Example Lab", "x", run)).Should().Contain(p => p.Contains("names no runId"));
    }

    [Fact]
    public void Review1_M5_a_decision_is_recorded_once_and_two_decisions_in_one_second_never_share_a_file()
    {
        var (_, pack) = Built();
        var passed = PackTrialGate.Decide(pack.Manifest, Arm(30), Arm(0), Now, pack.Digest);
        var failed = PackTrialGate.Decide(pack.Manifest, Arm(2), Arm(0), Now, pack.Digest);

        var first = PackActivationFolder.Write(_folder, passed, _host);
        PackActivationFolder.Write(_folder, passed, _host).Should().Be(first, "the very same decision is recorded already, whatever its new signature");
        PackActivationFolder.Write(_folder, failed, _host).Should().NotBe(first, "a failure in the same second never replaces a pass");

        Directory.GetFiles(_folder, "*.dsse.json").Should().HaveCount(2);
        Directory.GetFiles(_folder, "*.partial").Should().BeEmpty("a record appears whole or not at all");
        Activations().History(Id).Should().HaveCount(2);
    }

    [Fact]
    public void Review2_the_serve_gate_counts_a_certificate_for_a_pack_alone_but_never_for_a_family_member()
    {
        var (built, pack) = Built();
        var loaded = PackLoader.Load(Certify(built, pack), Trust());
        PackActivationFolder.Write(_folder, PackCertificates.Admit(loaded, Now).Decision!, _host);

        PackGateCheck.Of([loaded], Activations(), trustCertificatesFor: ["io.agentmemory"]).On.Should().Equal(Id);
        // A family's members are composed with AddFamily, where only the host's own trials count: so they are here too.
        var asMember = PackGateCheck.Of([loaded], Activations(), trustCertificatesFor: ["io.agentmemory"], familyMembers: new HashSet<string> { Id });
        asMember.On.Should().BeEmpty();
        asMember.Off.Should().ContainSingle().Which.ModuleId.Should().Be(Id);
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    private static readonly DateTimeOffset Earlier = Now.AddHours(-2);

    // An arm with consistent per-task evidence: items spread evenly, one right item on each of the first `hits` tasks and the
    // rest of the right items on them too. The cap is the one the declarations fix (review 7, G4): this pack declares no
    // traversal, so the format's 16; an arm that offers nothing is the baseline none, at 0, unless it is the host's (1 or more).
    private static CertificateArm ArmOf(int tasks, int hits, int offered, int correct, long p95, int? cap = null)
    {
        var o = Enumerable.Range(0, tasks).Select(i => offered / Math.Max(1, tasks) + (i < offered % Math.Max(1, tasks) ? 1 : 0)).ToArray();
        if (hits > tasks || correct < hits || correct > o.Take(hits).Sum() || o.Take(hits).Any(x => x == 0))
            throw new ArgumentException($"No arm of {tasks} tasks has {hits} hits and {correct} of {offered} items right.");
        var c = new int[tasks];
        for (var i = 0; i < hits; i++) c[i] = 1;
        for (var (left, i) = (correct - hits, 0); left > 0 && hits > 0; i = (i + 1) % hits)
            if (c[i] < o[i]) { c[i]++; left--; }
        return new(tasks, hits, offered, correct, p95, cap ?? (offered == 0 ? 0 : ModuleTraversal.MaximumOffered), PackTrialGate.BurstLength, [.. o.Select((x, i) => new CertificateTask(x, c[i]))]);
    }

    // Forty tasks of three items with the per-task evidence a certificate carries (review 5); none at all for 0 hits, the
    // baseline none, which offers nothing by design (review 7, G4). The cap is left to the declarations, as the runner does.
    private static PackTrialMeasurement Arm(int hits)
    {
        if (hits == 0)
            return new() { Tasks = 40, Hits = 0, Offered = 0, Correct = 0, P95Ms = 12.5, MaximumOffered = 0, Spacing = PackTrialGate.BurstLength, PerTask = [.. Enumerable.Repeat(new PackTrialTask(0, 0), 40)] };
        var arm = ArmOf(40, hits, 120, Math.Clamp(30, hits, 3 * hits), 12_500);
        return new()
        {
            Tasks = 40, Hits = hits, Offered = 120, Correct = arm.Correct, P95Ms = 12.5, Spacing = PackTrialGate.BurstLength,
            PerTask = [.. arm.PerTask.Select(t => new PackTrialTask(t.Offered, t.Correct))],
        };
    }

    // A trial at each commit's parent: the only kind certified (review 4).
    private static PackActivation Decision(LoadedPack pack) => PackTrialGate.Decide(pack.Manifest, Arm(24), Arm(0), Earlier, pack.Digest) with { History = "parent" };

    private static PackDecisionBasis Basis() => new()
    {
        Kind = "certificate", Certificate = "sha256:" + new string('c', 64), Evaluator = "Someone", EvaluatorKey = new string('d', 64),
        EvaluatedAt = Earlier, TaskSource = "elsewhere",
    };

    // A decision that does not say how its tasks were drawn is taken as a trial at each commit's parent here; the refusal of the
    // others is tested on its own (Only_a_trial_at_each_commits_parent_is_certified).
    private byte[] Certify(byte[] built, LoadedPack pack, PackActivation? decision = null) =>
        PackCertificates.Issue(built, Trust(), (decision ?? Decision(pack)) is { History: null } unsaid ? unsaid with { History = "parent" } : decision ?? Decision(pack),
            _evaluator.PrivateKeyPem, "Example Lab", "40 changes from the lab's repository");

    [Fact]
    public void Only_a_trial_at_each_commits_parent_is_certified()
    {
        // Review 4, L-4: the library refuses what the tool refused, a trial that saw the future and one that does not say.
        var (built, pack) = Built();
        foreach (var (history, says) in new[] { ("head", "head"), ((string?)null, "does not say") })
            FluentActions.Invoking(() => PackCertificates.Issue(built, Trust(), Decision(pack) with { History = history }, _evaluator.PrivateKeyPem, "Example Lab", "x"))
                .Should().Throw<PackLoadException>().Which.Problems.Should().Contain(problem => problem.Contains(says));
    }

    private static IReadOnlyList<string> Refused(Func<object> act) => FluentActions.Invoking(act).Should().Throw<PackLoadException>().Which.Problems;

    private (byte[] Built, LoadedPack Pack) Built(string? trial = Trial, string? version = null)
    {
        var source = new Dictionary<string, byte[]>(PackAuthoring.FromModuleBundle(Directory.EnumerateFiles(MartialArts, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(MartialArts, f).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes, StringComparer.Ordinal)), StringComparer.Ordinal)
        {
            ["card.json"] = Encoding.UTF8.GetBytes(PackAdviceTests.Card.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n"),
        };
        if (trial is not null) source["evals/trial.json"] = Encoding.UTF8.GetBytes(trial);
        if (version is not null)
        {
            var manifest = JsonNode.Parse(source["pack.json"])!.AsObject();
            manifest["version"] = version;
            source["pack.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        }
        var built = PackAuthoring.Build(source, _publisher.PrivateKeyPem);
        return (built, PackLoader.Load(built, Trust()));
    }

    private PackTrust Trust(bool evaluator = true, string evaluatorRole = "evaluator", string evaluatorPrefix = "io.agentmemory")
    {
        var keys = new JsonArray(new JsonObject
        {
            ["keyId"] = _publisher.KeyId, ["publicKey"] = _publisher.PublicKeyPem, ["idPrefixes"] = new JsonArray("io.agentmemory"), ["roles"] = new JsonArray("publisher"),
        });
        if (evaluator)
            keys.Add(new JsonObject
            {
                ["keyId"] = _evaluator.KeyId, ["publicKey"] = _evaluator.PublicKeyPem, ["idPrefixes"] = new JsonArray(evaluatorPrefix), ["roles"] = new JsonArray(evaluatorRole),
            });
        return PackTrust.Parse(Encoding.UTF8.GetBytes(new JsonObject { ["format"] = "agentmemory.pack.trust/1.1", ["keys"] = keys }.ToJsonString()));
    }

    private PackActivationFolder Activations() => new(_folder, _host.ExportSubjectPublicKeyInfoPem());

    // The gate alone, for a pack that goes on: composing it fully would need a store for its ontology.
    private (IReadOnlyList<string> On, IReadOnlyList<SwitchedOffModule> Off) Gate(LoadedPack pack, params string[] certificateTrust)
    {
        var (on, off) = PackTrialGate.Apply(new ModuleSetBuilder().AddPack(pack).Modules, Activations(), new PackTrialPolicy([], certificateTrust ?? [], null));
        return ([.. on.Select(r => r.Manifest.Id)], off);
    }

    private ModuleSet Gated(Action<ModuleSetBuilder> add) => Compose(m =>
    {
        add(m);
        m.RequireTrials(Activations());
    });

    private static ModuleSet Compose(Action<ModuleSetBuilder> add)
    {
        var services = new ServiceCollection();
        services.AddAgentMemoryModules(add);
        return services.BuildServiceProvider().GetRequiredService<ModuleSet>();
    }
}
