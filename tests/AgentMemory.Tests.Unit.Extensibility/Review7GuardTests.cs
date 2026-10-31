using System.Text;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// One guard for each fix of review 7 that had none (the root-cause analysis, C2: a fix counted as done when the reviewer's
/// example passed). Each test fails if its fix is taken back: it names the finding, the one place the rule now lives, and the
/// input that broke it.
/// </summary>
public sealed class Review7GuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    private static ModuleManifest Manifest(string targets, string extra = "") => ModuleManifest.Parse(
        """{"format":"agentmemory.module/v1alpha1","id":"com.example.base","version":"1.0.0","sdk":"[0.1,0.2)","trial":{"measure":"test-impact","tasks":40,"targets":"""
        + targets + extra + "}}");

    // Forty tasks drawn every eight commits; `hits` of them offer two items with one right, the rest offer nothing.
    private static PackTrialMeasurement Arm(int hits, double p95 = 10, int spacing = PackTrialGate.BurstLength) => new()
    {
        Tasks = 40, Hits = hits, Correct = hits, Offered = 2 * hits, P95Ms = p95, Spacing = spacing,
        PerTask = [.. Enumerable.Range(0, 40).Select(i => i < hits ? new PackTrialTask(2, 1) : new PackTrialTask(0, 0))],
    };

    [Fact]
    public void H6_a_host_floor_is_judged_on_the_tasks_a_decision_ran_not_on_what_its_pack_declares()
    {
        var manifest = Manifest("""{"hitRateMicro":500000}""");
        var decision = PackTrialGate.Decide(manifest, Arm(30), Arm(0), Now);
        var floor = new PackTrialFloor { Tasks = 40 };
        PackTrialGate.Below(manifest.Trial!, floor).Should().BeNull("the declaration claims forty tasks");

        var fewer = decision with { With = decision.With! with { Tasks = 12 } };

        PackTrialGate.Below(fewer, floor).Should().Contain("12 tasks ran, 40 required");
    }

    [Fact]
    public void M13_a_lift_floor_is_not_met_by_a_lift_against_a_baseline_that_offers_nothing()
    {
        var manifest = Manifest("""{"hitRateMicro":500000,"liftMicro":100000}""");
        var decision = PackTrialGate.Decide(manifest, Arm(30), Arm(0), Now);

        PackTrialGate.Below(decision, new PackTrialFloor { Targets = new ModuleTrialTargets { LiftMicro = 100_000 } })
            .Should().Contain("baseline that offers nothing");
        PackTrialGate.Below(manifest.Trial!, new PackTrialFloor { Targets = new ModuleTrialTargets { LiftMicro = 100_000 } })
            .Should().Contain("not this host's own retrieval");
    }

    [Fact]
    public void M11_the_caps_every_bound_rests_on_are_the_declarations()
    {
        var manifest = Manifest("""{"hitRateMicro":500000}""");

        FluentActions.Invoking(() => PackTrialGate.Decide(manifest, Arm(30) with { MaximumOffered = 2 }, Arm(0), Now))
            .Should().Throw<ArgumentException>().WithMessage("*records a cap of 2*maxItems*");
        FluentActions.Invoking(() => PackTrialGate.Decide(manifest, Arm(30), Arm(4), Now))
            .Should().Throw<ArgumentException>().WithMessage("*baseline of none offers nothing*");
        var host = Manifest("""{"hitRateMicro":500000},"baseline":"host" """.TrimEnd());
        FluentActions.Invoking(() => PackTrialGate.Decide(host, Arm(30), Arm(4) with { MaximumOffered = 0 }, Now))
            .Should().Throw<ArgumentException>().WithMessage("*host's own retrieval*cap of 1 or more*");
        PackTrialGate.Decide(manifest, Arm(30), Arm(0), Now).With!.MaximumOffered.Should().Be(ModuleTraversal.MaximumOffered, "left out, the cap is the declared one");
    }

    [Fact]
    public void L11_every_arm_of_a_trial_was_drawn_at_one_spacing_within_the_history_window()
    {
        var manifest = Manifest("""{"hitRateMicro":500000}""");

        FluentActions.Invoking(() => PackTrialGate.Decide(manifest, Arm(30), Arm(0, spacing: 1), Now))
            .Should().Throw<ArgumentException>().WithMessage("*one spacing*");
        FluentActions.Invoking(() => PackTrialGate.Decide(manifest, Arm(30, spacing: 60), Arm(0, spacing: 60), Now))
            .Should().Throw<ArgumentException>().WithMessage($"*span more than the {PackTrialGate.HistoryWindow}*");
        PackTrialGate.Decide(manifest, Arm(30, spacing: 51), Arm(0, spacing: 51), Now).Active.Should().BeTrue("51 x 39 = 1,989 is within the window");
    }

    [Fact]
    public void L16_a_recheck_runs_the_intake_the_decision_ran()
    {
        var decision = PackTrialGate.Decide(Manifest("""{"hitRateMicro":500000}"""), Arm(30), Arm(0), Now);
        PackTrialGate.Unmet(decision).Should().BeNull();

        var respaced = decision with { Without = decision.Without! with { Spacing = 1 } };

        PackTrialGate.Unmet(respaced).Should().Contain("one spacing");
    }

    [Fact]
    public void L13_a_latency_exactly_at_its_budget_passes_whatever_the_binary_rounding()
    {
        // 2.3 - 1.3 is 0.9999999999999998 in binary; in whole microseconds it is 1000, at the budget of 1 ms (a budget is whole
        // milliseconds since feature 02).
        var decision = PackTrialGate.Decide(Manifest("""{"hitRateMicro":500000,"addedP95Ms":1}"""), Arm(30, p95: 2.3), Arm(0, p95: 1.3), Now);

        decision.Checks.Single(c => c.Target == "addedP95Ms").Met.Should().BeTrue();
    }

    [Fact]
    public void L17_a_floor_s_targets_are_held_to_the_rule_a_manifest_s_are()
    {
        foreach (var bad in new ModuleTrialTargets[] { new() { HitRateMicro = -1 }, new() { HitRateMicro = 1_500_000 }, new() { AddedP95Ms = 0 } })
            FluentActions.Invoking(() => new ModuleSetBuilder().RequireTrialFloor(new PackTrialFloor { Targets = bad }))
                .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void H7_content_read_without_verifying_it_is_bounded_before_it_is_parsed()
    {
        var line = Encoding.UTF8.GetBytes("{\"text\":\"" + new string('x', (1 << 20) + 1) + "\"}\n");
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["pack.json"] = """{"format":"agentmemory.pack/1.2","id":"io.example.big","version":"1.0.0","kind":"knowledge","sdk":"[0.1,0.2)"}"""u8.ToArray(),
            ["records/big.jsonl"] = line,
        };
        files[PackPaths.FilesList] = FilesList.Build("io.example.big", "1.0.0", files);
        var archive = StrictZip.Write(files);

        FluentActions.Invoking(() => PackDiff.Compare(archive, archive)).Should().Throw<PackLoadException>()
            .Which.Problems.Should().Contain(p => p.Contains("a line longer than 1 MiB"));
        FluentActions.Invoking(() => PackScan.Scan(archive)).Should().Throw<PackLoadException>()
            .Which.Problems.Should().Contain(p => p.Contains("a line longer than 1 MiB"));
        var many = Enumerable.Range(0, 65).ToDictionary(i => $"records/r{i:00}.json", _ => new byte[1 << 20], StringComparer.Ordinal);
        PackContentBounds.Problems(many, verified: false).Should().Contain(p => p.Contains("at most 64 MiB"));
        PackContentBounds.Problems(many, verified: true).Should().BeEmpty("verified content was bounded by the publisher's signature and verification's own caps");
    }

    [Fact]
    public void M6_every_signature_holds_the_writer_s_rules_not_build_alone()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["pack.json"] = """{"format":"agentmemory.pack/1.3","id":"io.example.later","version":"1.0.0","kind":"knowledge","sdk":"[0.1,0.2)"}"""u8.ToArray(),
        };
        files[PackPaths.FilesList] = FilesList.Build("io.example.later", "1.0.0", files);

        FluentActions.Invoking(() => PackBuilder.Sign(files, key.ExportPkcs8PrivateKeyPem(), "assurer", Now))
            .Should().Throw<InvalidDataException>().WithMessage("*signs only what it understands*");
    }

    [Fact]
    public void M7_the_one_writer_never_writes_an_archive_a_reader_refuses()
    {
        // A sparse file deflates past the ratio every reader allows: the writer stores it, and it reads back.
        var sparse = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["data"] = new byte[4 << 20] };
        StrictZip.Read(StrictZip.Write(sparse), PackLimits.Default)["data"].Should().HaveCount(4 << 20);

        // Seventeen envelopes: more than any reader accepts, refused before the archive is returned.
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["pack.json"] = """{"format":"agentmemory.pack/1.2","id":"io.example.many","version":"1.0.0","kind":"knowledge","sdk":"[0.1,0.2)"}"""u8.ToArray(),
        };
        files[PackPaths.FilesList] = FilesList.Build("io.example.many", "1.0.0", files);
        for (var i = 0; i < 17; i++)
        {
            using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            PackBuilder.Sign(files, key.ExportPkcs8PrivateKeyPem(), "assurer", Now);
        }

        FluentActions.Invoking(() => PackBuilder.Pack(files)).Should().Throw<InvalidDataException>().WithMessage("No reader would accept this pack*envelopes*");
    }

    [Fact]
    public void L23_the_statistic_s_reference_names_the_statistic_the_gate_runs()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentMemory.slnx"))) directory = directory.Parent;
        var reference = File.ReadLines(Path.Combine(directory!.FullName, "eng", "trial-statistics", "reference.py")).First();

        reference.Should().Contain("(statistic " + PackTrialGate.Statistic + ")");
    }
}
