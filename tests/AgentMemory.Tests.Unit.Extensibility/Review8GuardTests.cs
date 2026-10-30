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
}
