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
        ""","trial":{"measure":"test-impact","tasks":40,"targets":{"hitRateMicro":500000,"precisionMicro":250000,"liftMicro":250000,"addedP95Ms":250}}""");
    private static readonly ModuleManifest Dependent = Manifest(Top, ""","requires":{"modules":{"com.example.base":"[1,2)"}}""");

    /// <summary>
    /// Review 1, M17: the declarations digest is pinned. It hashes the SDK's object model, so a change to how this SDK writes a
    /// manifest (a new property, a renamed one, a default filled in) changes it, and every trialled pack on every host would stay
    /// off until it is trialled again. This vector fails first: such a change needs a migration, never a silent release.
    /// </summary>
    [Fact]
    public void TheDeclarationsDigestIsPinned() =>
        PackTrialGate.DigestOf(Trialled).Should().Be(GoldenTrialledDigest, "the SDK's manifest serialization changed: see the remarks on PackTrialGate.DigestOf");

    private const string GoldenTrialledDigest = "jcs-sha256:dd4def6a8004cf640dbbff3ccdbc7eb2c1dfdbcb98cae98a240ea57b151af016";

    /// <summary>Review 2: the same pin over a full manifest (ontology, views, traversal, trial), the code map pack's own.</summary>
    [Fact]
    public void TheCodeMapDeclarationsDigestIsPinned() =>
        PackTrialGate.DigestOf(AgentMemory.Packs.CodeMap.CodeMapPack.Manifest).Should().Be(GoldenCodeMapDigest,
            "the SDK's manifest serialization changed, or the code map's declarations did: see the remarks on PackTrialGate.DigestOf");

    private const string GoldenCodeMapDigest = "jcs-sha256:c9446c0e46ee07b0d45a763dc8ede4960f5b71066e121f74152e5b19e73056f3";

    [Fact]
    public void Review6_with_a_confidence_no_regression_needs_a_tolerance_and_is_then_a_bound()
    {
        ModuleManifest Upgrade(double tolerance) => Manifest(Base,
            $$$""","trial":{"measure":"test-impact","tasks":40,"confidencePermille":950,"targets":{"hitRateMicro":500000},"upgrade":{"noRegression":true,"toleranceMicro":{{{(long)Math.Round(tolerance * 1_000_000)}}}}}""");
        var arm = Arm(30, 32, 60, 10);
        var previous = new PackTrialPrevious { Version = "0.9.0", ManifestDigest = "jcs-sha256:" + new string('a', 64), With = arm };

        // H1: at tolerance 0 the check fell back to the estimate, which no bound backs (a real drop of 0.1 passed a fifth of the
        // time), so with a confidence a tolerance of 0 is refused; without one it stays the estimate check it says it is.
        Upgrade(0).Validate().Should().Contain(p => p.Contains("$.trial.upgrade.tolerance") && p.Contains("above 0"));
        Upgrade(0.05).Validate().Should().BeEmpty();
        var equal = PackTrialGate.Decide(Upgrade(0) with { Trial = Upgrade(0).Trial! with { ConfidencePermille = null } }, arm, Arm(0, 0, 0, 0), Now, previous: previous);
        equal.Checks.Where(c => c.Target.StartsWith("noRegression", StringComparison.Ordinal)).Should().OnlyContain(c => c.Met && c.Estimate == null,
            "without a confidence an upgrade as good as its predecessor passes on its estimates");

        // Above 0, a non-inferiority test: two versions that agree on every task leave the unseen task the upgrade could break,
        // so on 40 tasks a tolerance of 0.05 cannot be shown (review 5, M4).
        var loose = PackTrialGate.Decide(Upgrade(0.05), arm, Arm(0, 0, 0, 0), Now, previous: previous);
        loose.Checks.Single(c => c.Target == "noRegression.hitRate").Estimate.Should().Be(0);
        loose.Checks.Single(c => c.Target == "noRegression.hitRate").Observed.Should().BeLessThan(-0.05);
    }

    [Fact]
    public void Review2_the_precision_difference_bound_never_collapses_to_its_estimate()
    {
        // Three tasks offered one item each, all right; seven offered nothing; the baseline offered nothing at all.
        var tasks = Enumerable.Range(0, 10).Select(i => i < 3 ? new PackTrialTask(1, 1) : new PackTrialTask(0, 0)).ToList();
        var with = new PackTrialMeasurement { Tasks = 10, Hits = 3, Offered = 3, Correct = 3, P95Ms = 1, PerTask = tasks };
        var without = new PackTrialMeasurement { Tasks = 10, Hits = 0, Offered = 0, Correct = 0, P95Ms = 0, PerTask = [.. Enumerable.Repeat(new PackTrialTask(0, 0), 10)], MaximumOffered = 0 };
        var bound = PackTrialGate.PrecisionDifferenceLower(with, without, 0.975);

        bound.Should().BeApproximately(PackTrialGate.PrecisionLower(tasks, 16, 0.975), 1e-9, "against an arm that offers nothing by design, the bound is the arm's own");
        bound.Should().BeLessThan(0.6, "three items cannot show a precision lift near 1 with 95% confidence (it was 1.000)");
        PackTrialGate.PrecisionDifferenceLower(with, without with { MaximumOffered = 3 }, 0.975).Should().BeLessThan(bound,
            "an arm that could offer but did not keeps its unseen task (review 6, M5)");
    }

    [Fact]
    public void Review3_the_precision_difference_bound_stays_honest_when_the_arms_are_strongly_correlated()
    {
        // The pack is right on the easy half and wrong on the hard half; so is the baseline, less sharply. Newcombe's margin
        // shrank to the difference of two floored margins here (0.104); the paired cluster-robust bound is 0.011.
        var with = Measured([.. Enumerable.Repeat(new PackTrialTask(8, 8), 5), .. Enumerable.Repeat(new PackTrialTask(8, 2), 5)], cap: 8);
        var grep = Measured([.. Enumerable.Repeat(new PackTrialTask(3, 2), 5), .. Enumerable.Repeat(new PackTrialTask(3, 1), 5)], cap: 3);

        var bound = PackTrialGate.PrecisionDifferenceLower(with, grep, 0.95);

        bound.Should().BeLessThan(0.05, "a precisionLift target of 0.1 must not pass at 95% on these ten tasks");
        bound.Should().BeGreaterThan(-0.3, "ten tasks, the unseen one weighted 1.5 (review 6): wide, not empty");
    }

    [Fact]
    public void Review5_the_code_maps_real_trial_under_the_hedged_statistic()
    {
        // The code map's trial on this repository, each task at its commit's parent (round 3 re-run): offered and right per task,
        // three a task at most in both arms. Precision 0.351 against grep's 0.191. Round 4's statistic bounded the lift at 0.063
        // and met the declared 0.05; the harness then showed it passing a lift equal to its target up to 15% of the time. With the
        // unseen task at the worst both caps allow (review 5) and weighted to hold the level exactly (review 6), the bound is
        // -0.003: forty tasks do not show a lift of 0.05.
        static PackTrialMeasurement Arm(string counts)
        {
            var tasks = counts.Split(' ').Select(c => new PackTrialTask(c[0] - '0', c[1] - '0')).ToList();
            return Measured(tasks, cap: 3);
        }
        var with = Arm("30 31 20 21 32 22 21 31 22 20 11 31 31 21 20 10 00 00 20 21 00 20 22 21 11 11 10 21 30 30 30 10 00 21 11 11 30 20 31 11");
        var grep = Arm("30 31 31 31 31 31 30 30 32 30 31 31 31 31 30 30 30 30 30 30 30 30 30 31 31 31 30 21 31 30 31 30 00 31 30 30 31 30 32 21");

        PackTrialGate.PrecisionDifferenceLower(with, grep, 0.95).Should().BeApproximately(-0.0034, 0.0005);
        PackTrialGate.PrecisionLower(with.PerTask!, 3, 0.95).Should().BeApproximately(0.2390, 0.0005, "below the declared 0.25");
        // Review 6, M7: two copies of this very map on these tasks bound their precision difference at -0.134, more than the
        // -0.086 of copies that offer all three items on every task.
        PackTrialGate.PrecisionDifferenceLower(with, with, 0.95).Should().BeApproximately(-0.1343, 0.0005);
        // Review 6, H4: these forty were the most recent eligible commits, one after another. Counted in bursts of eight, as
        // such a trial now is, they show nothing: the lift bound falls to -0.64, the precision bound far below its target.
        var (inRow, grepInRow) = (with with { Spacing = 1 }, grep with { Spacing = 1 });
        PackTrialGate.PrecisionDifferenceLower(inRow, grepInRow, 0.95).Should().BeLessThan(-0.5);
        PackTrialGate.PrecisionLower(inRow, 0.95).Should().BeLessThan(PackTrialGate.PrecisionLower(with, 0.95));
        PackTrialGate.RateLower(with.Hits, with.Tasks, 0.95).Should().BeApproximately(0.4088, 0.0005, "above the declared 0.35");
    }

    [Fact]
    public void Review3_the_difference_bound_never_collapses_where_no_design_effect_can_be_measured()
    {
        // All right in one task, or on every task, against an arm that offered nothing: the arm's own bound, the one its
        // precision target uses.
        PackTrialGate.PrecisionDifferenceLower(Measured([new(3, 3)]), Measured([new(0, 0)], cap: 0), 0.95).Should().BeLessThan(0.3);
        PackTrialGate.PrecisionDifferenceLower(Measured([new(1, 1), new(1, 1), new(1, 1)]), Measured([new(0, 0), new(0, 0), new(0, 0)], cap: 0), 0.95)
            .Should().BeApproximately(PackTrialGate.PrecisionLower([new(1, 1), new(1, 1), new(1, 1)], 16, 0.95), 1e-12).And.BeLessThan(0.3);
        // Two arms the same on every task: below 0, by the unseen task the first could break (review 5, M4).
        var same = Measured([new(2, 1), new(3, 2), new(1, 0), new(2, 1), new(3, 2), new(1, 0)], cap: 3);
        PackTrialGate.PrecisionDifferenceLower(same, same, 0.95).Should().BeLessThan(0);
    }

    // Tasks drawn a burst apart, independent, unless told otherwise (review 6, H4).
    private static PackTrialMeasurement Measured(IReadOnlyList<PackTrialTask> tasks, int? cap = null, int spacing = PackTrialGate.BurstLength) => new()
    {
        Tasks = tasks.Count, Hits = tasks.Count(t => t.Correct > 0), Offered = tasks.Sum(t => t.Offered), Correct = tasks.Sum(t => t.Correct), P95Ms = 1,
        PerTask = tasks, MaximumOffered = cap, Spacing = spacing,
    };

    // Forty tasks with these totals: the items spread evenly, one right item on each of the first `hits` tasks and the rest of
    // the right items on them too, so the per-task results agree with the totals.
    // Impossible totals get no per-task results, so the gate's own checks answer for them.
    private static PackTrialMeasurement Arm(int hits, int correct, int offered, double p95)
    {
        var tasks = Enumerable.Range(0, 40).Select(i => offered < 0 ? 0 : offered / 40 + (i < offered % 40 ? 1 : 0)).ToArray();
        if (hits < 0 || hits > 40 || correct < hits || correct > offered || correct > tasks.Take(hits).Sum() || tasks.Take(hits).Any(o => o == 0) || hits == 0 && correct > 0)
            return new() { Tasks = 40, Hits = hits, Correct = correct, Offered = offered, P95Ms = p95, Spacing = PackTrialGate.BurstLength };
        var right = new int[40];
        for (var i = 0; i < hits; i++) right[i] = 1;
        for (var (left, i) = (correct - hits, 0); left > 0; i = (i + 1) % Math.Max(1, hits))
            if (right[i] < tasks[i]) { right[i]++; left--; }
        return new()
        {
            Tasks = 40, Hits = hits, Correct = correct, Offered = offered, P95Ms = p95, Spacing = PackTrialGate.BurstLength,
            PerTask = [.. tasks.Select((o, i) => new PackTrialTask(o, right[i]))], // the cap is the one the declarations fix, as the runner records it (review 7, G4)
        };
    }

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
        var careful = Manifest(Base, ""","trial":{"measure":"test-impact","tasks":40,"baseline":"host","confidencePermille":950,"targets":{"hitRateMicro":500000,"precisionLiftMicro":50000}}"""
            + ""","ontology":{"labelPrefix":"Code","types":[{"name":"CodeFile","scope":"shared","render":"{path}","properties":{"path":{"type":"string","required":true}}}]}"""
            + ""","traversal":{"anchors":[{"type":"CodeFile","property":"path","match":"exact"}],"hubs":["CodeFile"],"maxItems":3}""");
        careful.Validate().Should().BeEmpty();
        // The host's own retrieval offered at most three a task as well: a cap as the runner records it (review 7, G4).
        var host = Arm(20, 20, 100, 30) with { MaximumOffered = 3 };

        // 22 of 40 is 0.55, but its one-sided 95% Clopper-Pearson bound is about 0.41: not enough for 0.5.
        var lucky = PackTrialGate.Decide(careful, Arm(22, 40, 100, 60), host, Now);
        lucky.Active.Should().BeFalse();
        var hit = lucky.Checks.Single(c => c.Target == "hitRate");
        hit.Estimate.Should().Be(0.55);
        hit.Observed.Should().BeApproximately(0.409, 0.001);
        // Precision 0.40 against the baseline's 0.20: lift 0.20, met.
        lucky.Checks.Single(c => c.Target == "precisionLift").Met.Should().BeTrue();

        var solid = PackTrialGate.Decide(careful, Arm(34, 40, 100, 60), host, Now);
        solid.Active.Should().BeTrue();
    }

    [Fact]
    public void The_rate_bound_is_Clopper_Pearsons()
    {
        PackTrialGate.RateLower(0, 10, 0.95).Should().Be(0);
        PackTrialGate.RateLower(30, 40, 0.95).Should().BeApproximately(0.6129, 0.0005);
        PackTrialGate.RateLower(40, 40, 0.95).Should().BeApproximately(Math.Pow(0.05, 1.0 / 40), 1e-9);
    }

    [Fact]
    public void A_trial_names_a_known_baseline_and_confidence()
    {
        Manifest(Base, ""","trial":{"measure":"test-impact","baseline":"vibes","targets":{"hitRateMicro":500000}}""").Validate().Should().Contain(p => p.Contains("$.trial.baseline"));
        Manifest(Base, ""","trial":{"measure":"test-impact","confidencePermille":500,"targets":{"hitRateMicro":500000}}""").Validate().Should().Contain(p => p.Contains("$.trial.confidence"));
    }

    [Fact]
    public void With_confidence_a_difference_target_is_met_only_by_its_lower_bound()
    {
        // Review 1, M4: lift and precisionLift keep the same promise as the rates.
        var careful = Manifest(Base, ""","trial":{"measure":"test-impact","tasks":40,"baseline":"host","confidencePermille":950,"targets":{"liftMicro":50000}}""");
        var loose = Manifest(Base, ""","trial":{"measure":"test-impact","tasks":40,"baseline":"host","targets":{"liftMicro":50000}}""");
        // 24 of 40 against 20 of 40: a lift of 0.10, which a 95% bound on 40 tasks cannot tell from nothing.
        var withArm = Arm(24, 30, 80, 20);
        var withoutArm = Arm(20, 25, 90, 10);
        PackTrialGate.Decide(loose, withArm, withoutArm, Now).Active.Should().BeTrue("on its estimate the lift is met");
        var bounded = PackTrialGate.Decide(careful, withArm, withoutArm, Now);
        bounded.Active.Should().BeFalse();
        var check = bounded.Checks.Single(c => c.Target == "lift");
        check.Estimate.Should().Be(0.1);
        check.Observed.Should().BeLessThan(0.05);
        // The bound is paired: arms that hit the same tasks are surer of their difference than arms that hit different ones.
        IReadOnlyList<PackTrialTask> Tasks(int hits, bool last) => [.. Enumerable.Range(0, 40).Select(i => new PackTrialTask(2, (last ? i >= 40 - hits : i < hits) ? 1 : 0))];
        PackTrialMeasurement Of(IReadOnlyList<PackTrialTask> tasks) => new() { Tasks = 40, Hits = tasks.Count(t => t.Correct > 0), Offered = 80, Correct = tasks.Sum(t => t.Correct), P95Ms = 1, PerTask = tasks };
        PackTrialGate.HitDifferenceLower(Of(Tasks(24, false)), Of(Tasks(20, false)), 0.95)
            .Should().BeGreaterThan(PackTrialGate.HitDifferenceLower(Of(Tasks(24, false)), Of(Tasks(20, true)), 0.95));
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
        var thin = PackTrialGate.Decide(Trialled, Measured([.. Enumerable.Repeat(new PackTrialTask(2, 1), 5)]) with { P95Ms = 60 },
            Measured([.. Enumerable.Repeat(new PackTrialTask(0, 0), 5)]) with { P95Ms = 30 }, Now);
        thin.Active.Should().BeFalse();
        thin.Reason.Should().Contain("needs 10");
    }

    [Fact]
    public void A_trial_needs_a_measure_and_a_target_in_range()
    {
        Manifest(Base, ""","trial":{"measure":"vibes","targets":{"hitRateMicro":500000}}""").Validate().Should().Contain(p => p.Contains("$.trial.measure"));
        Manifest(Base, ""","trial":{"measure":"test-impact","targets":{"hitRateMicro":1500000}}""").Validate().Should().Contain(p => p.Contains("$.trial.targets.hitRate"));
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
        var weakened = Manifest(Base, ""","trial":{"measure":"test-impact","tasks":40,"targets":{"hitRateMicro":100000}}""");
        Write(PackTrialGate.Decide(weakened, Arm(0, 0, 0, 60), Arm(0, 0, 0, 30), Now.AddMinutes(5)), _host, "weak");

        var set = Activate(new PackActivationFolder(_folder, PublicKey()));

        set.Modules.Select(m => m.Manifest.Id).Should().Contain(Base);
    }

    [Fact]
    public void A_record_for_other_content_does_not_count()
    {
        var weakened = Manifest(Base, ""","trial":{"measure":"test-impact","tasks":40,"targets":{"hitRateMicro":100000}}""");
        Write(PackTrialGate.Decide(weakened, Arm(30, 32, 77, 60), Arm(0, 0, 0, 30), Now), _host);

        var set = Activate(new PackActivationFolder(_folder, PublicKey()));

        set.SwitchedOff.Single(o => o.ModuleId == Base).Reason.Should().Contain("other content");
    }

    [Fact]
    public void Review6_a_host_floor_leaves_off_a_trial_that_claims_less()
    {
        // M8: a pack chose its own targets, so {"hitRateMicro":0} without a confidence always passed. The host sets what a trial
        // must claim before it counts.
        var weak = Manifest(Base, ""","trial":{"measure":"test-impact","tasks":40,"targets":{"hitRateMicro":0}}""");
        Write(PackTrialGate.Decide(weak, Arm(0, 0, 0, 60), Arm(0, 0, 0, 30), Now), _host, "weak");
        Write(PackTrialGate.Decide(Trialled, Arm(30, 32, 77, 60), Arm(0, 0, 0, 30), Now), _host, "real");
        ModuleSet With(ModuleManifest manifest, PackTrialFloor floor)
        {
            var services = new ServiceCollection();
            services.AddAgentMemoryModules(m => m.Add(manifest, _ => { }).RequireTrials(new PackActivationFolder(_folder, PublicKey()))
                .RequireTrialsFor(Base).RequireTrialFloor(floor));
            return services.BuildServiceProvider().GetRequiredService<ModuleSet>();
        }

        var floor = new PackTrialFloor { ConfidencePermille = 950, Targets = new ModuleTrialTargets { HitRateMicro = 300_000 } };
        With(weak, floor).SwitchedOff.Single().Reason.Should().Be("its trial claims less than this host requires: no confidence, 0.95 required; hitRate 0 below 0.3");
        With(Trialled, floor).SwitchedOff.Single().Reason.Should().Contain("no confidence, 0.95 required");
        With(Trialled, new PackTrialFloor { Targets = new ModuleTrialTargets { HitRateMicro = 300_000, AddedP95Ms = 500 } }).Modules.Should().ContainSingle();
        With(Trialled, new PackTrialFloor { Tasks = 60 }).SwitchedOff.Single().Reason.Should().Contain("40 tasks, 60 required");
        FluentActions.Invoking(() => With(Trialled, new PackTrialFloor { ConfidencePermille = 800 })).Should().Throw<ArgumentException>();
        var unbound = new ServiceCollection();
        unbound.AddAgentMemoryModules(m => m.Add(Trialled, _ => { }).RequireTrialFloor(floor));
        FluentActions.Invoking(() => unbound.BuildServiceProvider().GetRequiredService<ModuleSet>()).Should().Throw<ModuleActivationException>()
            .Which.Message.Should().Contain("call RequireTrials too");
        // Review 7: a floor with trials optional let a pack that declares no trial compose past it.
        var optional = new ServiceCollection();
        optional.AddAgentMemoryModules(m => m.Add(Trialled, _ => { }).RequireTrials(new PackActivationFolder(_folder, PublicKey())).RequireTrialFloor(floor));
        FluentActions.Invoking(() => optional.BuildServiceProvider().GetRequiredService<ModuleSet>()).Should().Throw<ModuleActivationException>()
            .Which.Message.Should().Contain("needs RequireTrialsFor");
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
