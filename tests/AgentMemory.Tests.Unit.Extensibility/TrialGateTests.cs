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
    public void A_trial_with_too_few_tasks_decides_nothing()
    {
        var thin = PackTrialGate.Decide(Trialled, Arm(30, 32, 77, 60) with { Tasks = 5, Hits = 5 }, Arm(0, 0, 0, 30), Now);
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
