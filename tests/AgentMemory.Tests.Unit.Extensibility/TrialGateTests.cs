using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentMemory.Extensibility;
using AgentMemory.Extensibility.Modules;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// Trial-gated activation (pack format v1.1 §4, claim 2): a pack that declares a trial is switched on only with a passing
/// decision for exactly its manifest, signed by the host; whatever needs a pack left off is left off too.
/// </summary>
public sealed class TrialGateTests : IDisposable
{
    private const string Base = "com.example.base";
    private const string Top = "com.example.top";
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 18, 0, 0, TimeSpan.Zero);
    private readonly string _folder = Directory.CreateTempSubdirectory("trial-gate-").FullName;
    private readonly ECDsa _host = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _host.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private static ModuleManifest Manifest(string id, string extra = "") => ModuleManifest.Parse(
        $$"""{"format":"agentmemory.module/v1alpha1","id":"{{id}}","version":"1.0.0","sdk":"[0.1,0.2)"{{extra}}}""");

    private static readonly ModuleManifest Trialled = Manifest(Base,
        ""","trial":{"measure":"test-impact","tasks":40,"targets":{"hitRate":0.5,"precision":0.25,"lift":0.25,"addedP95Ms":250}}""");
    private static readonly ModuleManifest Dependent = Manifest(Top, ""","requires":{"modules":{"com.example.base":"[1,2)"}}""");

    /// <summary>
    /// Review 1, M17: the declarations digest is pinned. It hashes the SDK's object model, so a change to how this SDK writes a
    /// manifest (a new property, a renamed one, a default filled in) changes it, and every trialled pack on every host would stay
    /// off until it is trialled again. This vector fails first: such a change needs a migration, never a silent release.
    /// </summary>
    [Fact]
    public void TheDeclarationsDigestIsPinned() =>
        PackTrialGate.DigestOf(Trialled).Should().Be(GoldenTrialledDigest, "the SDK's manifest serialization changed: see the remarks on PackTrialGate.DigestOf");

    private const string GoldenTrialledDigest = "jcs-sha256:4f8b018552e494ff47e9c0b4889937d22914ff93a04faf88161b5fb7daa7df51";

    private static PackTrialMeasurement Arm(int hits, int correct, int offered, double p95) =>
        new() { Tasks = 40, Hits = hits, Correct = correct, Offered = offered, P95Ms = p95 };

    [Fact]
    public void A_pack_goes_on_only_when_every_target_is_met()
    {
        var pass = PackTrialGate.Decide(Trialled, Arm(30, 32, 77, 60), Arm(0, 0, 0, 30), Now);
        pass.Active.Should().BeTrue();
        pass.Checks.Should().HaveCount(4).And.OnlyContain(c => c.Met);

        var weak = PackTrialGate.Decide(Trialled, Arm(8, 9, 70, 60), Arm(0, 0, 0, 30), Now);
        weak.Active.Should().BeFalse();
        weak.Reason.Should().Contain("hitRate 0.2 below 0.5").And.Contain("lift 0.2 below 0.25");
    }

    [Fact]
    public void With_confidence_a_rate_target_is_met_only_by_its_lower_bound()
    {
        var careful = Manifest(Base, ""","trial":{"measure":"test-impact","tasks":40,"baseline":"host","confidence":0.95,"targets":{"hitRate":0.5,"precisionLift":0.05}}""");
        careful.Validate().Should().BeEmpty();

        // 22 of 40 is 0.55, but its one-sided 95% Wilson lower bound is about 0.42: not enough for 0.5.
        var lucky = PackTrialGate.Decide(careful, Arm(22, 40, 100, 60), Arm(20, 20, 100, 30), Now);
        lucky.Active.Should().BeFalse();
        var hit = lucky.Checks.Single(c => c.Target == "hitRate");
        hit.Estimate.Should().Be(0.55);
        hit.Observed.Should().BeApproximately(0.422, 0.001);
        // Precision 0.40 against the baseline's 0.20: lift 0.20, met.
        lucky.Checks.Single(c => c.Target == "precisionLift").Met.Should().BeTrue();

        var solid = PackTrialGate.Decide(careful, Arm(34, 40, 100, 60), Arm(20, 20, 100, 30), Now);
        solid.Active.Should().BeTrue();
    }

    [Fact]
    public void The_Wilson_bound_matches_its_formula()
    {
        PackTrialGate.WilsonLower(0, 10, 1.6448536).Should().Be(0);
        PackTrialGate.WilsonLower(30, 40, 1.6448536).Should().BeApproximately(0.6240, 0.001);
        PackTrialGate.WilsonLower(40, 40, 1.6448536).Should().BeApproximately(0.9366, 0.001);
    }

    [Fact]
    public void A_trial_names_a_known_baseline_and_confidence()
    {
        Manifest(Base, ""","trial":{"measure":"test-impact","baseline":"vibes","targets":{"hitRate":0.5}}""").Validate().Should().Contain(p => p.Contains("$.trial.baseline"));
        Manifest(Base, ""","trial":{"measure":"test-impact","confidence":0.5,"targets":{"hitRate":0.5}}""").Validate().Should().Contain(p => p.Contains("$.trial.confidence"));
    }

    [Fact]
    public void With_confidence_a_difference_target_is_met_only_by_its_lower_bound()
    {
        // Review 1, M4: lift and precisionLift keep the same promise as the rates.
        var careful = Manifest(Base, ""","trial":{"measure":"test-impact","tasks":40,"baseline":"host","confidence":0.95,"targets":{"lift":0.05}}""");
        var loose = Manifest(Base, ""","trial":{"measure":"test-impact","tasks":40,"baseline":"host","targets":{"lift":0.05}}""");
        // 24 of 40 against 20 of 40: a lift of 0.10, which a 95% bound on 40 tasks cannot tell from nothing.
        var withArm = Arm(24, 30, 80, 20);
        var withoutArm = Arm(20, 25, 90, 10);
        PackTrialGate.Decide(loose, withArm, withoutArm, Now).Active.Should().BeTrue("on its estimate the lift is met");
        var bounded = PackTrialGate.Decide(careful, withArm, withoutArm, Now);
        bounded.Active.Should().BeFalse();
        var check = bounded.Checks.Single(c => c.Target == "lift");
        check.Estimate.Should().Be(0.1);
        check.Observed.Should().BeLessThan(0.05);
        // Paired results narrow the bound: the same tasks hit in both arms are not counted as independent evidence.
        IReadOnlyList<PackTrialTask> Tasks(int hits) => [.. Enumerable.Range(0, 40).Select(i => new PackTrialTask(2, i < hits ? 1 : 0))];
        var paired = PackTrialGate.HitDifferenceLower(withArm with { PerTask = Tasks(24), Offered = 80, Correct = 24 },
            withoutArm with { PerTask = Tasks(20), Offered = 80, Correct = 20 }, 1.6448536);
        paired.Should().BeGreaterThan(PackTrialGate.HitDifferenceLower(withArm, withoutArm, 1.6448536));
        // A large, consistent lift still passes at 95%.
        PackTrialGate.Decide(careful, Arm(36, 40, 80, 20), Arm(12, 20, 90, 10), Now).Active.Should().BeTrue();
    }

    [Fact]
    public void A_decision_needs_two_arms_of_the_same_tasks_with_possible_counts()
    {
        FluentActions.Invoking(() => PackTrialGate.Decide(Trialled, Arm(30, 32, 77, 60), null!, Now)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => PackTrialGate.Decide(Trialled, Arm(30, 32, 77, 60), Arm(0, 0, 0, 30) with { Tasks = 39 }, Now)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => PackTrialGate.Decide(Trialled, Arm(41, 32, 77, 60), Arm(0, 0, 0, 30), Now)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => PackTrialGate.Decide(Trialled, Arm(30, 80, 77, 60), Arm(0, 0, 0, 30), Now)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_trial_with_too_few_tasks_decides_nothing()
    {
        var thin = PackTrialGate.Decide(Trialled, Arm(30, 32, 77, 60) with { Tasks = 5, Hits = 5 }, Arm(0, 0, 0, 30) with { Tasks = 5 }, Now);
        thin.Active.Should().BeFalse();
        thin.Reason.Should().Contain("needs 10");
    }

    [Fact]
    public void A_trial_needs_a_measure_and_a_target_in_range()
    {
        Manifest(Base, ""","trial":{"measure":"vibes","targets":{"hitRate":0.5}}""").Validate().Should().Contain(p => p.Contains("$.trial.measure"));
        Manifest(Base, ""","trial":{"measure":"test-impact","targets":{"hitRate":1.5}}""").Validate().Should().Contain(p => p.Contains("$.trial.targets.hitRate"));
        Trialled.Validate().Should().BeEmpty();
    }

    [Fact]
    public void Without_a_record_the_pack_and_what_needs_it_stay_off()
    {
        var set = Activate(new PackActivationFolder(_folder, PublicKey()));

        set.Modules.Should().BeEmpty();
        set.SwitchedOff.Should().BeEquivalentTo([
            new SwitchedOffModule(Base, "1.0.0", "no trial on this host yet"),
            new SwitchedOffModule(Top, "1.0.0", "it needs com.example.base, which is off"),
        ]);
    }

    [Fact]
    public void A_passing_record_signed_by_the_host_switches_the_pack_on()
    {
        Write(PackTrialGate.Decide(Trialled, Arm(30, 32, 77, 60), Arm(0, 0, 0, 30), Now), _host);

        var set = Activate(new PackActivationFolder(_folder, PublicKey()));

        set.Modules.Select(m => m.Manifest.Id).Should().BeEquivalentTo([Base, Top]);
        set.SwitchedOff.Should().BeEmpty();
    }

    [Fact]
    public void A_failed_trial_keeps_the_pack_off_with_its_reason()
    {
        Write(PackTrialGate.Decide(Trialled, Arm(8, 9, 70, 60), Arm(0, 0, 0, 30), Now), _host);

        var set = Activate(new PackActivationFolder(_folder, PublicKey()));

        set.SwitchedOff.Single(o => o.ModuleId == Base).Reason.Should().StartWith("its trial failed: hitRate 0.2 below 0.5");
    }

    [Fact]
    public void Editing_a_record_or_signing_it_with_another_key_cannot_switch_a_pack_on()
    {
        var failed = PackTrialGate.Decide(Trialled, Arm(8, 9, 70, 60), Arm(0, 0, 0, 30), Now);
        var path = Write(failed, _host);
        var envelope = JsonDocument.Parse(File.ReadAllBytes(path)).RootElement;
        var payload = Encoding.UTF8.GetString(Convert.FromBase64String(envelope.GetProperty("payload").GetString()!));
        var forged = payload.Replace("\"Active\":false", "\"Active\":true", StringComparison.Ordinal);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            payloadType = "application/vnd.in-toto+json", payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(forged)),
            signatures = new[] { new { keyid = "host", sig = envelope.GetProperty("signatures")[0].GetProperty("sig").GetString() } },
        }));
        using (var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            Write(PackTrialGate.Decide(Trialled, Arm(30, 32, 77, 60), Arm(0, 0, 0, 30), Now.AddMinutes(1)), stranger, "stranger");

        var activations = new PackActivationFolder(_folder, PublicKey());

        activations.Ignored.Should().HaveCount(2);
        Activate(activations).Modules.Should().BeEmpty();
    }

    [Fact]
    public void A_later_trial_of_other_content_does_not_shadow_the_passing_one()
    {
        // The real pack passes; a weakened copy under the same id and version is trialled later into the same folder (PACK-review 1.7).
        Write(PackTrialGate.Decide(Trialled, Arm(30, 32, 77, 60), Arm(0, 0, 0, 30), Now), _host, "real");
        var weakened = Manifest(Base, ""","trial":{"measure":"test-impact","tasks":40,"targets":{"hitRate":0.1}}""");
        Write(PackTrialGate.Decide(weakened, Arm(0, 0, 0, 60), Arm(0, 0, 0, 30), Now.AddMinutes(5)), _host, "weak");

        var set = Activate(new PackActivationFolder(_folder, PublicKey()));

        set.Modules.Select(m => m.Manifest.Id).Should().Contain(Base);
    }

    [Fact]
    public void A_record_for_other_content_does_not_count()
    {
        var weakened = Manifest(Base, ""","trial":{"measure":"test-impact","tasks":40,"targets":{"hitRate":0.1}}""");
        Write(PackTrialGate.Decide(weakened, Arm(30, 32, 77, 60), Arm(0, 0, 0, 30), Now), _host);

        var set = Activate(new PackActivationFolder(_folder, PublicKey()));

        set.SwitchedOff.Single(o => o.ModuleId == Base).Reason.Should().Contain("other content");
    }

    [Fact]
    public void A_host_that_does_not_require_trials_composes_as_before()
    {
        var services = new ServiceCollection();
        services.AddAgentMemoryModules(m => m.Add(Trialled, _ => { }).Add(Dependent, _ => { }));
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ModuleSet>().Modules.Should().HaveCount(2);
    }

    private ModuleSet Activate(IPackActivations activations)
    {
        var services = new ServiceCollection();
        services.AddAgentMemoryModules(m => m.Add(Trialled, _ => { }).Add(Dependent, _ => { }).RequireTrials(activations));
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<ModuleSet>();
    }

    private string PublicKey() => _host.ExportSubjectPublicKeyInfoPem();

    private string Write(PackActivation activation, ECDsa key, string name = "host")
    {
        var statement = PackActivationFolder.Statement(activation);
        var sig = key.SignData(PackActivationFolder.Pae(statement), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        var path = Path.Combine(_folder, $"{activation.ModuleId}@{activation.Version}-{name}-{activation.DecidedAt:yyyyMMddHHmmss}.dsse.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            payloadType = "application/vnd.in-toto+json", payload = Convert.ToBase64String(statement),
            signatures = new[] { new { keyid = name, sig = Convert.ToBase64String(sig) } },
        }));
        return path;
    }
}
