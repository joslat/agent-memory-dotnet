#pragma warning disable AMEXT001
using System.Security.Cryptography;
using System.Text.Json;
using AgentMemory.Abstractions.Options;
using AgentMemory.Extensibility;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Packs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// Review 8's guard audit (A3 to A5): rules whose guards passed with the rule broken at a door. Each test here goes through the
/// door the auditor named, so deleting the rule there fails it; eng/guards/mutants.json records that mutation.
/// </summary>
public sealed class Review8GuardTests : IDisposable
{
    private const string Base = "com.example.base";
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    private readonly string _folder = Directory.CreateTempSubdirectory("review8-").FullName;
    private readonly ECDsa _host = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _host.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private static ModuleManifest Manifest(int tasks, string version = "1.0.0") => ModuleManifest.Parse(
        "{\"format\":\"agentmemory.module/v1alpha1\",\"id\":\"" + Base + "\",\"version\":\"" + version + "\",\"sdk\":\"[0.1,0.2)\",\"trial\":{\"measure\":\"test-impact\",\"tasks\":" + tasks + ",\"targets\":{\"hitRate\":0.5}}}");

    private static PackTrialMeasurement Arm(int tasks, int hits) => new()
    {
        Tasks = tasks, Hits = hits, Correct = hits, Offered = 2 * hits, P95Ms = 10, Spacing = PackTrialGate.BurstLength,
        PerTask = [.. Enumerable.Range(0, tasks).Select(i => i < hits ? new PackTrialTask(2, 1) : new PackTrialTask(0, 0))],
    };

    private void Write(PackActivation decision, string name) => PackActivationFolder.Write(Path.Combine(_folder), decision, _host);

    [Fact]
    public void A3_the_module_set_judges_a_host_floor_on_the_tasks_a_decision_ran()
    {
        // A record this host signed for the declarations of 40 tasks, whose evidence ran 12: the declaration meets a floor of 40,
        // the evidence does not. The floor holds only if composition judges the evidence (PackTrialGate.Apply).
        var ran = PackTrialGate.Decide(Manifest(12), Arm(12, 9), Arm(12, 0), Now);
        Write(ran with { ManifestDigest = PackTrialGate.DigestOf(Manifest(40)) }, "short");

        var services = new ServiceCollection();
        services.AddAgentMemoryModules(m => m.Add(Manifest(40), _ => { }).RequireTrials(new PackActivationFolder(_folder, _host.ExportSubjectPublicKeyInfoPem()))
            .RequireTrialsFor(Base).RequireTrialFloor(new PackTrialFloor { Tasks = 40 }));
        var set = services.BuildServiceProvider().GetRequiredService<ModuleSet>();

        set.SwitchedOff.Should().ContainSingle().Which.Reason.Should().Contain("12 tasks ran, 40 required");
    }

    [Fact]
    public void A5_two_snapshots_of_one_module_are_refused_whoever_holds_them()
    {
        static LoadedPack Shot(string id) => new()
        {
            Id = id, Version = "1.0.0", Digest = "sha256:" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id))).ToLowerInvariant(),
            Kind = "snapshot", Manifest = ModuleManifest.Parse(
                "{\"format\":\"agentmemory.module/v1alpha1\",\"id\":\"" + id + "\",\"version\":\"1.0.0\",\"sdk\":\"[0.1,0.2)\",\"requires\":{\"modules\":{\"com.example.code\":\"[1,2)\"}}}"),
        };

        PackMountPlan.Problems([Shot("io.example.repo-a"), Shot("io.example.repo-b")]).Should().ContainSingle()
            .Which.Should().Contain("io.example.repo-a and io.example.repo-b are snapshots of com.example.code");
    }

    [Fact]
    public void A5_the_gate_holds_packs_together_by_the_mount_plan()
    {
        static LoadedPack Version(string version) => new()
        {
            Id = Base, Version = version, Digest = "sha256:" + new string(version[0], 64), Kind = "knowledge", Manifest = Manifest(40, version),
        };

        FluentActions.Invoking(() => PackGateCheck.Of([Version("1.0.0"), Version("2.0.0")], new PackActivationFolder(_folder, _host.ExportSubjectPublicKeyInfoPem())))
            .Should().Throw<PackLoadException>().Which.Problems.Should().ContainSingle().Which.Should().Contain("comes more than once");
    }

    [Fact]
    public async Task A4_a_pack_memory_walk_leaves_out_the_records_an_expired_carrier_brought()
    {
        const string Code = """
            {"format":"agentmemory.module/v1alpha1","id":"com.example.code","version":"1.0.0","sdk":"[0.1,0.2)",
             "ontology":{"labelPrefix":"Code","types":[{"name":"CodeFile","scope":"shared","render":"{path}","properties":{"path":{"type":"string","required":true}}}]},
             "traversal":{"anchors":[{"type":"CodeFile","property":"path","match":"exact"}],"maxItems":3}}
            """;
        var record = new ModuleProposal
        {
            Kind = ProposalKind.Assert, Type = "CodeFile", Key = "src/a.cs",
            Properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["path"] = JsonSerializer.SerializeToElement("src/a.cs") },
            Source = new ProposalSource { Excerpt = "src/a.cs" },
        };
        var snapshot = new ModuleSnapshot
        {
            Module = ModuleManifest.Parse(Code), Source = "git:abc", Records = [record],
            Carrier = new PackCarrier { Id = "io.example.shot", Version = "1.0.0", Digest = "sha256:" + new string('c', 64), Signer = "publisher" },
        };
        var memory = PackMemory.From([], [snapshot]);
        ContextAnchor[] anchors = [ContextAnchor.Path("src/a.cs")];

        (await memory.WalkAsync(anchors, null, live: null, CancellationToken.None)).Hits.Should().ContainSingle();
        (await memory.WalkAsync(anchors, null, live: null, CancellationToken.None, liveCarrier: id => id != "io.example.shot")).Hits
            .Should().BeEmpty("the carrier expired, and its records take no part");
    }

    [Fact]
    public void A7_the_upgrade_chain_passes_over_a_version_below_the_host_floor()
    {
        // 1.1.0 passed its own trial on 12 tasks, below this host's floor of 40: it never passed here, so it is not the version an
        // upgrade replaces. 1.2.0 was compared with 1.0.0, the last version that did, and goes on; without the floor, 1.1.0 is
        // the version it replaces, and 1.2.0, compared with another, stays off. Removing the floor from the chain fails this.
        var first = PackTrialGate.Decide(Upgrading(40, "1.0.0"), Arm(40, 30), Arm(40, 0), Now);
        var below = PackTrialGate.Decide(Upgrading(12, "1.1.0"), Arm(12, 10), Arm(12, 0), Now.AddDays(1), previous: Previous(first, Arm(12, 10)));
        var upgrade = PackTrialGate.Decide(Upgrading(40, "1.2.0"), Arm(40, 31), Arm(40, 0), Now.AddDays(2), previous: Previous(first, Arm(40, 30)));
        (first.Active && below.Active && upgrade.Active).Should().BeTrue("each passes its own trial");
        foreach (var decision in new[] { first, below, upgrade }) Write(decision, decision.Version!);

        ComposeUpgrade(new PackTrialFloor { Tasks = 40 }).SwitchedOff.Should().BeEmpty();
        ComposeUpgrade(null).SwitchedOff.Should().ContainSingle("without the floor, 1.1.0 is the version 1.2.0 replaces");
    }

    [Fact]
    public void G2_only_this_host_s_own_trials_stand_as_the_version_an_upgrade_replaces()
    {
        // 1.1.0 was admitted on a certificate, in a namespace that trusts certificates. The gate sets such a decision aside once its
        // certificate is no longer the latest trusted one the pack carries; the chain, which cannot tell, took it as the version that
        // passed here all the same, so an upgrade compared with it went on and one compared with the running 1.0.0 stayed off.
        var first = PackTrialGate.Decide(Upgrading(40, "1.0.0"), Arm(40, 30), Arm(40, 0), Now);
        var certified = PackTrialGate.Decide(Upgrading(40, "1.1.0"), Arm(40, 30), Arm(40, 0), Now.AddDays(1), previous: Previous(first, Arm(40, 30))) with
        {
            Basis = new PackDecisionBasis
            {
                Kind = "certificate", Certificate = "sha256:" + new string('c', 64), Evaluator = "Someone", EvaluatorKey = new string('d', 64),
                EvaluatedAt = Now, TaskSource = "elsewhere",
            },
        };
        var upgrade = PackTrialGate.Decide(Upgrading(40, "1.2.0"), Arm(40, 31), Arm(40, 0), Now.AddDays(2), previous: Previous(first, Arm(40, 30)));
        foreach (var decision in new[] { first, certified, upgrade }) Write(decision, decision.Version!);

        ComposeUpgrade(null, trustCertificates: true).SwitchedOff.Should().BeEmpty("1.0.0, the last version this host's own trial passed, is the one 1.2.0 replaces");
    }

    private static ModuleManifest Upgrading(int tasks, string version) => ModuleManifest.Parse(
        "{\"format\":\"agentmemory.module/v1alpha1\",\"id\":\"" + Base + "\",\"version\":\"" + version + "\",\"sdk\":\"[0.1,0.2)\",\"trial\":{\"measure\":\"test-impact\",\"tasks\":"
        + tasks + ",\"targets\":{\"hitRate\":0.5},\"upgrade\":{\"noRegression\":true,\"tolerance\":0.02}}}");

    private static PackTrialPrevious Previous(PackActivation passed, PackTrialMeasurement measured) => new()
    {
        Version = passed.Version, ManifestDigest = passed.ManifestDigest, PackDigest = passed.PackDigest, With = measured,
    };

    // Version 1.2.0 composed against this test's activations: trials required for the base, a floor and certificate trust as given.
    private ModuleSet ComposeUpgrade(PackTrialFloor? floor, bool trustCertificates = false)
    {
        var services = new ServiceCollection();
        services.AddAgentMemoryModules(m =>
        {
            var builder = m.Add(Upgrading(40, "1.2.0"), _ => { }).RequireTrials(new PackActivationFolder(_folder, _host.ExportSubjectPublicKeyInfoPem()))
                .RequireTrialsFor(Base);
            if (floor is not null) builder.RequireTrialFloor(floor);
            if (trustCertificates) builder.TrustCertificatesFor(Base);
        });
        return services.BuildServiceProvider().GetRequiredService<ModuleSet>();
    }

    [Fact]
    public void A_file_one_byte_over_its_bound_is_refused_and_one_at_it_is_read()
    {
        // FILE-BOUNDED at its definition (the mutant suite: its named guard passed with the bound removed).
        var path = Path.Combine(_folder, "over.bin");
        File.WriteAllBytes(path, new byte[1025]);
        FluentActions.Invoking(() => AgentMemory.Extensibility.Modules.BoundedFile.Read(path, 1024, "A file"))
            .Should().Throw<InvalidDataException>().WithMessage("A file is larger than 1024 bytes.");
        AgentMemory.Extensibility.Modules.BoundedFile.Read(path, 1025, "A file").Should().HaveCount(1025);
    }

    [Fact]
    public void W2_neo4j_expansion_returns_narrow_rows_and_reads_whole_only_the_records_it_keeps()
    {
        // Match was narrowed in review 6; Expand, its twin, returned every tied neighbour whole when a tied key was wide. Both
        // expansion statements now return ids, keys, degrees and hits; the reader keeps the best per start and reads those whole.
        foreach (var statement in new[] { AgentMemory.Extensibility.Neo4j.Queries.ModuleTraversalQueries.Out("S", "LINKS", "T"),
                     AgentMemory.Extensibility.Neo4j.Queries.ModuleTraversalQueries.In("S", "LINKS", "T") })
        {
            statement.Should().NotContain("properties(", "an expansion row is narrow");
            statement.Should().Contain("AS id").And.Contain("AS rkey");
        }
    }
}
