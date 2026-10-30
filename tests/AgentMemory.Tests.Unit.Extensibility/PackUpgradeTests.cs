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

    // Precision 0.5 unless given; as many right items as hits at least (a hit is a right item).
    private static PackTrialMeasurement Arm(int hits, int? correct = null, int? offered = null) =>
        new() { Tasks = 40, Hits = hits, Correct = correct ?? hits, Offered = offered ?? 2 * (correct ?? hits), P95Ms = 10 };

    private static readonly PackTrialMeasurement Nothing = new() { Tasks = 40, Hits = 0, Correct = 0, Offered = 0, P95Ms = 5 };

    // Every arm of one trial was drawn at one spacing (review 7, G13).
    private static readonly PackTrialMeasurement NothingSpaced = Nothing with { Spacing = PackTrialGate.BurstLength };

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
        // Review 6: a certificate records targets in thousandths, so a manifest states them so (L9); and with a confidence, a
        // tolerance of 0 asks for a bound no measurement gives (H1).
        Manifest("1.1.0", ""","upgrade":{"noRegression":true,"tolerance":0.0504}""").Validate().Should().Contain(p => p.Contains("$.trial.upgrade.tolerance") && p.Contains("three decimals"));
        Manifest("1.1.0", ""","upgrade":{"noRegression":true,"tolerance":0},"confidence":0.95""").Validate().Should().Contain(p => p.Contains("$.trial.upgrade.tolerance"));
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

        PackTrialGate.Decide(Manifest("1.1.0"), Arm(30, correct: 30, offered: 63), Nothing, Now, previous: Previous(passed, Arm(30)))
            .Active.Should().BeFalse("a precision drop of 0.024 is beyond the 0.02 tolerance");
    }

    [Fact]
    public void Within_the_tolerance_an_upgrade_goes_on()
    {
        var passed = PackTrialGate.Decide(Manifest("1.0.0"), Arm(30), Nothing, Now);

        var upgrade = PackTrialGate.Decide(Manifest("1.1.0"), Arm(30, correct: 30, offered: 61), Nothing, Now, previous: Previous(passed, Arm(30, correct: 30, offered: 60)));

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
    public void A_first_version_is_not_held_to_a_predecessor()
    {
        Write(PackTrialGate.Decide(Manifest("1.1.0"), Arm(32), Nothing, Now));

        Compose(Manifest("1.1.0")).Modules.Should().ContainSingle("nothing passed here before it");
    }

    [Fact]
    public void When_neither_version_asks_for_the_rule_an_upgrade_composes_without_comparison()
    {
        Write(PackTrialGate.Decide(Manifest("1.0.0", upgrade: ""), Arm(30), Nothing, Now));
        Write(PackTrialGate.Decide(Manifest("1.2.0", upgrade: ""), Arm(31), Nothing, Now.AddMinutes(1)));

        Compose(Manifest("1.2.0", upgrade: "")).Modules.Should().ContainSingle();
    }

    [Fact]
    public void A_version_can_neither_drop_nor_loosen_the_rule_its_predecessor_was_held_to()
    {
        var passed = PackTrialGate.Decide(Manifest("1.0.0"), Arm(30), Nothing, Now); // tolerance 0.02
        Write(passed);

        // 1.1.0 declares no rule of its own and was never compared: still off.
        Write(PackTrialGate.Decide(Manifest("1.1.0", upgrade: ""), Arm(29), Nothing, Now.AddMinutes(1)));
        Compose(Manifest("1.1.0", upgrade: "")).SwitchedOff.Should().ContainSingle().Which.Reason.Should().Contain("must compare");

        // Compared, with a loose tolerance of its own (0.5): held to the predecessor's 0.02, a drop of 0.025 is too much.
        const string loose = ""","upgrade":{"noRegression":true,"tolerance":0.5}""";
        Write(PackTrialGate.Decide(Manifest("1.1.0", loose), Arm(29), Nothing, Now.AddMinutes(2), previous: Previous(passed, Arm(30))));
        Compose(Manifest("1.1.0", loose)).SwitchedOff.Should().ContainSingle().Which.Reason.Should().Contain("beyond the tolerance 0.02");
    }

    [Fact]
    public void The_rule_carries_down_the_whole_chain_not_one_version()
    {
        // Third review: 1.0.0 asks for no regression; 1.1.0 declares nothing and stays within it; 1.2.0 declares nothing either.
        var first = PackTrialGate.Decide(Manifest("1.0.0"), Arm(30), Nothing, Now);
        Write(first);
        var second = PackTrialGate.Decide(Manifest("1.1.0", upgrade: ""), Arm(30), Nothing, Now.AddMinutes(1), previous: Previous(first, Arm(30)));
        Write(second);
        Compose(Manifest("1.1.0", upgrade: "")).Modules.Should().ContainSingle();

        // 1.2.0 was never compared: held to the rule 1.1.0 was held to, so off.
        Write(PackTrialGate.Decide(Manifest("1.2.0", upgrade: ""), Arm(20), Nothing, Now.AddMinutes(2)));
        Compose(Manifest("1.2.0", upgrade: "")).SwitchedOff.Should().ContainSingle().Which.Reason.Should().Contain("must compare");
        // Compared, and worse than 1.1.0 by more than 0.02: still off.
        Write(PackTrialGate.Decide(Manifest("1.2.0", upgrade: ""), Arm(20), Nothing, Now.AddMinutes(3), previous: Previous(second, Arm(30))));
        Compose(Manifest("1.2.0", upgrade: "")).SwitchedOff.Should().ContainSingle().Which.Reason.Should().Contain("beyond the tolerance 0.02");
    }

    [Fact]
    public void The_same_version_with_other_content_is_an_upgrade_too()
    {
        var first = "sha256:" + new string('a', 64);
        var second = "sha256:" + new string('b', 64);
        var passed = PackTrialGate.Decide(Manifest("1.0.0"), Arm(30), Nothing, Now, first);
        Write(passed);
        Write(PackTrialGate.Decide(Manifest("1.0.0"), Arm(31), Nothing, Now.AddMinutes(1), second));

        ComposePack(Manifest("1.0.0"), second).SwitchedOff.Should().ContainSingle().Which.Reason.Should().Contain("must compare");
        Write(PackTrialGate.Decide(Manifest("1.0.0"), Arm(31), Nothing, Now.AddMinutes(2), second, Previous(passed, Arm(30))));
        ComposePack(Manifest("1.0.0"), second).Modules.Should().ContainSingle();
        FluentActions.Invoking(() => PackTrialGate.Decide(Manifest("1.0.0"), Arm(31), Nothing, Now, first, Previous(passed, Arm(30))))
            .Should().Throw<ArgumentException>("the same content is not its own predecessor");
    }

    [Fact]
    public void A_predecessor_whose_latest_decision_failed_does_not_count()
    {
        Write(PackTrialGate.Decide(Manifest("1.0.0"), Arm(30), Nothing, Now));
        Write(PackTrialGate.Decide(Manifest("1.0.0"), Arm(2), Nothing, Now.AddMinutes(1))); // the same content, trialled again: it fails now
        Write(PackTrialGate.Decide(Manifest("1.1.0"), Arm(31), Nothing, Now.AddMinutes(2)));

        Compose(Manifest("1.1.0")).Modules.Should().ContainSingle("no version passes here now, so 1.1.0 is a first version");
    }

    // Forty tasks of three items, one right item on each of `hits` tasks from `from` on: the per-task results paired bounds need.
    private static PackTrialMeasurement Tasked(int hits, int from = 0) => new()
    {
        Tasks = 40, Hits = hits, Correct = hits, Offered = 120, P95Ms = 10, Spacing = PackTrialGate.BurstLength,
        PerTask = [.. Enumerable.Range(0, 40).Select(i => new PackTrialTask(3, i >= from && i < from + hits ? 1 : 0))],
    };

    private const string Careful = ""","upgrade":{"noRegression":true,"tolerance":0.05},"confidence":0.95""";

    [Fact]
    public void Review5_the_chain_carries_the_confidence_too()
    {
        // M6: 1.0.0 asks for no regression within 0.05 at 95%; 1.1.0 declares neither. On the same tasks it hits 2 fewer of 40,
        // -0.05 on its estimate, which estimates alone would let through; held to its predecessor's confidence, the bound is
        // below -0.05, so it stays off.
        var passed = PackTrialGate.Decide(Manifest("1.0.0", Careful), Tasked(30), NothingSpaced, Now);
        passed.Active.Should().BeTrue(passed.Reason);
        Write(passed);
        var upgrade = PackTrialGate.Decide(Manifest("1.1.0", upgrade: ""), Tasked(28), NothingSpaced, Now.AddMinutes(1), previous: Previous(passed, Tasked(30)));
        upgrade.Active.Should().BeTrue("it declares no rule of its own, and meets its target");
        Write(upgrade);

        Compose(Manifest("1.1.0", upgrade: "")).SwitchedOff.Should().ContainSingle().Which.Reason.Should().Contain("beyond the tolerance 0.05");
    }

    [Fact]
    public void Review6_the_chain_holds_every_rule_each_as_declared()
    {
        // H1: 1.0.0 asks for no regression within 0.1 at 95%; 1.1.0 for no drop of its estimate (tolerance 0, no confidence).
        // The stricter of the two was a blend, tolerance 0 at 95%, which fell back to the estimate: 1.1.0 hits as many tasks as
        // 1.0.0, ten of them others, so its estimate does not drop while its bound is below -0.2. Each rule as declared: off.
        const string rule = ""","upgrade":{"noRegression":true,"tolerance":0.1},"confidence":0.95""";
        var passed = PackTrialGate.Decide(Manifest("1.0.0", rule), Tasked(30), NothingSpaced, Now);
        passed.Active.Should().BeTrue(passed.Reason);
        Write(passed);
        const string own = ""","upgrade":{"noRegression":true,"tolerance":0}""";
        var upgrade = PackTrialGate.Decide(Manifest("1.1.0", own), Tasked(30, from: 10), NothingSpaced, Now.AddMinutes(1), previous: Previous(passed, Tasked(30)));
        upgrade.Active.Should().BeTrue("its own rule, on the estimate, is met");
        Write(upgrade);

        Compose(Manifest("1.1.0", own)).SwitchedOff.Should().ContainSingle().Which.Reason.Should().Contain("beyond the tolerance 0.1 at 0.95");
    }

    [Fact]
    public void Review5_a_record_from_an_earlier_statistic_is_checked_again_from_its_evidence()
    {
        // M7: a record the round-4 statistic decided is neither trusted nor set aside: its verdict is derived again from what it
        // carries. 1.1.0 passed against 1.0.0 under that statistic and its evidence holds under this one, so it stays the version
        // that passed here: 1.2.0, compared only against 1.0.0, stays off (it went on: 1.1.0 waited for a new trial and the
        // chain skipped it). Two versions that agree on forty tasks bound their hit rates' difference at -0.086, and their
        // precisions' at -0.364, hedged at the format's cap of 16 for a pack that declares no traversal: so the tolerance is 0.4.
        const string rule = ""","upgrade":{"noRegression":true,"tolerance":0.4},"confidence":0.95""";
        var first = PackTrialGate.Decide(Manifest("1.0.0", rule), Tasked(30), NothingSpaced, Now);
        Write(first);
        var second = PackTrialGate.Decide(Manifest("1.1.0", rule), Tasked(30), NothingSpaced, Now.AddMinutes(1), previous: Previous(first, Tasked(30)));
        second.Active.Should().BeTrue(second.Reason);
        Write(second with { Statistic = "agentmemory.trial.bounds/2026-10-29" });
        Compose(Manifest("1.1.0", rule)).Modules.Should().ContainSingle();
        Write(PackTrialGate.Decide(Manifest("1.2.0", rule), Tasked(30), NothingSpaced, Now.AddMinutes(2), previous: Previous(first, Tasked(30))));
        Compose(Manifest("1.2.0", rule)).SwitchedOff.Should().ContainSingle().Which.Reason.Should().Contain("but 1.1.0").And.Contain("is the version that passed here");

        // Where it does not, the pass is taken back (never given): 24 hits of 40 bound the hit rate below 0.5 at 95%.
        var weak = PackTrialGate.Decide(Manifest("2.0.0", Careful), Tasked(24), NothingSpaced, Now.AddMinutes(2));
        weak.Active.Should().BeFalse();
        Write(weak with { Active = true, Reason = "every target met", Statistic = "agentmemory.trial.bounds/2026-10-29" });
        Compose(Manifest("2.0.0", Careful)).SwitchedOff.Should().ContainSingle().Which.Reason.Should()
            .Contain("does not meet its targets under " + PackTrialGate.Statistic).And.Contain("hitRate");
    }

    private ModuleSet ComposePack(ModuleManifest manifest, string packDigest)
    {
        var services = new ServiceCollection();
        services.AddAgentMemoryModules(m => m.AddDefinitionPack(manifest, packDigest, "developer 1a2b")
            .RequireTrials(new PackActivationFolder(_folder, _host.ExportSubjectPublicKeyInfoPem())));
        return services.BuildServiceProvider().GetRequiredService<ModuleSet>();
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
