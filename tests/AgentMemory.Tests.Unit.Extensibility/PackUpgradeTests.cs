using System.Security.Cryptography;
using System.Text.Json;
using AgentMemory.Extensibility;
using AgentMemory.Extensibility.Modules;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// PACK-upgrade-gate (pack format v1.2 §7, claim 6; review 1.6): a new version that declares <c>upgrade.noRegression</c> goes
/// on only if, on the same tasks, it does not do worse than the version that passed on this host before it, and only with a
/// decision that compared it against exactly that version.
/// </summary>
public sealed class PackUpgradeTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
    private const string Upgrade = ""","upgrade":{"noRegression":true,"tolerance":0.02}""";
    private readonly string _folder = Directory.CreateTempSubdirectory("pack-upgrade-").FullName;
    private readonly ECDsa _host = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _host.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private static ModuleManifest Manifest(string version, string upgrade = Upgrade) => ModuleManifest.Parse(
        """{"format":"agentmemory.module/v1alpha1","id":"com.example.map","version":""" + "\"" + version + "\""
        + ""","sdk":"[0.1,0.2)","trial":{"measure":"test-impact","tasks":40,"targets":{"hitRate":0.5}""" + upgrade + "}}");

    private static PackTrialMeasurement Arm(int hits, int correct = 10, int offered = 20) =>
        new() { Tasks = 40, Hits = hits, Correct = correct, Offered = offered, P95Ms = 10 };

    private static readonly PackTrialMeasurement Nothing = new() { Tasks = 40, Hits = 0, Correct = 0, Offered = 0, P95Ms = 5 };

    private static PackTrialPrevious Previous(PackActivation passed, PackTrialMeasurement measured) => new()
    {
        Version = passed.Version, ManifestDigest = passed.ManifestDigest, PackDigest = passed.PackDigest, With = measured,
    };

    [Fact]
    public void A_trial_declares_its_upgrade_rule_and_a_tolerance_out_of_range_is_refused()
    {
        Manifest("1.1.0").Trial!.Upgrade.Should().Be(new ModuleTrialUpgrade { NoRegression = true, Tolerance = 0.02 });
        Manifest("1.1.0").Validate().Should().BeEmpty();
        Manifest("1.1.0", ""","upgrade":{"noRegression":true,"tolerance":0.9}""").Validate().Should().Contain(p => p.Contains("$.trial.upgrade.tolerance"));
        FluentActions.Invoking(() => Manifest("1.1.0", ""","upgrade":{"noRegression":true,"window":3}""")).Should().Throw<Exception>()
            .Which.Message.Should().Contain("window");
    }

    [Fact]
    public void An_upgrade_that_does_worse_than_the_active_version_on_the_same_tasks_stays_off()
    {
        var passed = PackTrialGate.Decide(Manifest("1.0.0"), Arm(30), Nothing, Now);
        // Measured in the same run: the old version now hits 30 of 40, the new one 26 of 40: above its own target, but worse.
        var upgrade = PackTrialGate.Decide(Manifest("1.1.0"), Arm(26), Nothing, Now, previous: Previous(passed, Arm(30)));

        upgrade.Active.Should().BeFalse();
        upgrade.Reason.Should().Contain("noRegression.hitRate -0.1 below -0.02");
        upgrade.Checks.Single(c => c.Target == "hitRate").Met.Should().BeTrue("it meets its own target; the regression is what stops it");
        upgrade.Previous!.Version.Should().Be("1.0.0");

        PackTrialGate.Decide(Manifest("1.1.0"), Arm(30, correct: 10, offered: 21), Nothing, Now, previous: Previous(passed, Arm(30)))
            .Active.Should().BeFalse("a precision drop of 0.024 is beyond the 0.02 tolerance");
    }

    [Fact]
    public void Within_the_tolerance_an_upgrade_goes_on()
    {
        var passed = PackTrialGate.Decide(Manifest("1.0.0"), Arm(30), Nothing, Now);

        var upgrade = PackTrialGate.Decide(Manifest("1.1.0"), Arm(30, correct: 20, offered: 41), Nothing, Now, previous: Previous(passed, Arm(30, correct: 20, offered: 40)));

        upgrade.Active.Should().BeTrue(upgrade.Reason);
        upgrade.Checks.Should().Contain(c => c.Target == "noRegression.precision" && c.Met);
    }

    [Fact]
    public void The_previous_version_is_earlier_and_measured_on_the_same_tasks()
    {
        var passed = PackTrialGate.Decide(Manifest("1.0.0"), Arm(30), Nothing, Now);

        FluentActions.Invoking(() => PackTrialGate.Decide(Manifest("1.0.0"), Arm(30), Nothing, Now, previous: Previous(passed, Arm(30))))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => PackTrialGate.Decide(Manifest("1.1.0"), Arm(30), Nothing, Now, previous: Previous(passed, Arm(30) with { Tasks = 39 })))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void At_composition_an_upgrade_needs_a_decision_that_compared_it_with_the_version_that_passed_here()
    {
        var passed = PackTrialGate.Decide(Manifest("1.0.0"), Arm(30), Nothing, Now);
        Write(passed);

        // A passing decision for 1.1.0 that compared against nothing: the gate keeps it off.
        Write(PackTrialGate.Decide(Manifest("1.1.0"), Arm(32), Nothing, Now.AddMinutes(1)));
        Compose(Manifest("1.1.0")).SwitchedOff.Should().ContainSingle().Which.Reason
            .Should().Be("it upgrades 1.0.0, which passed here; its trial must compare against that version");

        // Compared against other content under the same version number: still off.
        Write(PackTrialGate.Decide(Manifest("1.1.0"), Arm(32), Nothing, Now.AddMinutes(2),
            previous: Previous(passed with { ManifestDigest = "jcs-sha256:" + new string('0', 64) }, Arm(30))));
        Compose(Manifest("1.1.0")).SwitchedOff.Should().ContainSingle().Which.Reason.Should().Contain("is the version that passed here");

        // Compared against exactly 1.0.0, and no worse: on.
        Write(PackTrialGate.Decide(Manifest("1.1.0"), Arm(32), Nothing, Now.AddMinutes(3), previous: Previous(passed, Arm(30))));
        Compose(Manifest("1.1.0")).Modules.Should().ContainSingle().Which.Manifest.Version.Value.Should().Be("1.1.0");
    }

    [Fact]
    public void A_first_version_or_a_trial_without_the_rule_is_not_held_to_a_predecessor()
    {
        Write(PackTrialGate.Decide(Manifest("1.1.0"), Arm(32), Nothing, Now));
        Compose(Manifest("1.1.0")).Modules.Should().ContainSingle("nothing passed here before it");

        Write(PackTrialGate.Decide(Manifest("1.0.0"), Arm(30), Nothing, Now.AddMinutes(1)));
        Write(PackTrialGate.Decide(Manifest("1.2.0", upgrade: ""), Arm(31), Nothing, Now.AddMinutes(2)));
        Compose(Manifest("1.2.0", upgrade: "")).Modules.Should().ContainSingle("its trial does not ask for the rule");
    }

    private ModuleSet Compose(ModuleManifest manifest)
    {
        var services = new ServiceCollection();
        services.AddAgentMemoryModules(m => m.Add(manifest, _ => { }).RequireTrials(new PackActivationFolder(_folder, _host.ExportSubjectPublicKeyInfoPem())));
        return services.BuildServiceProvider().GetRequiredService<ModuleSet>();
    }

    private void Write(PackActivation activation)
    {
        var statement = PackActivationFolder.Statement(activation);
        var sig = _host.SignData(PackActivationFolder.Pae(statement), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        File.WriteAllText(Path.Combine(_folder, $"{activation.Version}-{activation.DecidedAt:HHmmss}.dsse.json"), JsonSerializer.Serialize(new
        {
            payloadType = "application/vnd.in-toto+json", payload = Convert.ToBase64String(statement),
            signatures = new[] { new { keyid = "host", sig = Convert.ToBase64String(sig) } },
        }));
    }
}
