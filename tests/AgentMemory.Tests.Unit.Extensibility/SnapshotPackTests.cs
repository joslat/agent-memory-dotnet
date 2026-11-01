using System.Text;
using System.Text.Json.Nodes;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Packs.CodeMap;
using AgentMemory.Packs.Format;
using AgentMemory.Packs.Work;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// Snapshot packs (pack format v1.2 §3, PACK-snapshot): a definition's records for one source at one commit, shared as a signed
/// .ampack. The code map of a repository round-trips through one with exactly its records; the host imports it only into the
/// definition it names, in the range it names, and checks every record against that definition before anything is written.
/// </summary>
public sealed class SnapshotPackTests
{
    private const string Store = "scip-dotnet nuget Acme.Map . Acme/Map/MapStore#";
    private const string StoreTest = "scip-dotnet nuget Acme.Map.Tests . Acme/Map/Tests/MapStoreTests#Remove_keeps_history().";
    private readonly PackSigningKey _key = PackAuthoring.CreateKey();

    private static ModuleSnapshot CodeMap(string source)
    {
        var builder = new CodeMapBuilder();
        builder.AddProject("src/Map/Map.csproj", "Acme.Map");
        builder.AddFile("src/Map/MapStore.cs", "src/Map/Map.csproj");
        builder.AddFile("tests/MapStoreTests.cs", null);
        builder.AddSymbol(Store, "MapStore", "Acme.Map.MapStore", CodeMapSymbolKind.Class, "Acme.Map", "src/Map/MapStore.cs", 3, ["Close", "Remove"]);
        builder.AddTest(StoreTest, "MapStoreTests.Remove_keeps_history", "tests/MapStoreTests.cs", 7, "xunit", [Store]);
        return builder.Build(source);
    }

    private PackTrust Trust() => PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(_key.PublicKeyPem, "io.example")));

    private Dictionary<string, byte[]> Source(ModuleSnapshot snapshot) =>
        new(PackAuthoring.FromSnapshot(snapshot, "io.example.acme-map", "1.0.0", repository: "https://example.com/acme/map", commit: "0a1b2c3d4e5f"),
            StringComparer.Ordinal);

    [Fact]
    public void A_snapshot_s_records_name_the_usage_policy_of_the_pack_that_carried_them()
    {
        // Feature 10, review H1: the policy goes with the records wherever they are stored, so a host that imports them and
        // composes the definition another way still asks it.
        var source = Source(CodeMap("git:local"));
        source["policy.json"] = Encoding.UTF8.GetBytes("""{"format":"agentmemory.pack.policy/1","rules":[{"allow":["read","rehydrate:local"],"audience":["*"]}]}""");
        var loaded = PackLoader.Load(PackAuthoring.Build(source, _key.PrivateKeyPem), Trust());
        var stamped = loaded.ToSnapshot(CodeMapPack.Manifest).Stamped().ToList();
        loaded.PolicyDigest.Should().StartWith("sha256:");
        stamped.Should().NotBeEmpty().And.OnlyContain(r => r.Source.Pack!.UsagePolicy == loaded.PolicyDigest);
        new ModuleSetBuilder().UsePackPolicy(loaded).PackPolicies.Should().ContainSingle(p => p.Digest == loaded.PolicyDigest);
    }

    [Fact]
    public void A_code_map_snapshot_round_trips_through_an_ampack_with_exactly_its_records()
    {
        var snapshot = CodeMap("git:local");

        var loaded = PackLoader.Load(PackAuthoring.Build(Source(snapshot), _key.PrivateKeyPem), Trust());
        var back = loaded.ToSnapshot(CodeMapPack.Manifest);

        loaded.Kind.Should().Be("snapshot");
        loaded.Knowledge.Should().BeNull();
        loaded.SnapshotSource.Should().Be("git:0a1b2c3d4e5f");
        back.Source.Should().Be("git:0a1b2c3d4e5f");
        back.Module.Should().BeSameAs(CodeMapPack.Manifest);
        back.Digest.Should().Be(snapshot.Digest, "the same records, property for property and link for link");
        back.Records.Should().HaveCount(snapshot.Records.Count);
        back.Records.Should().OnlyContain(r => r.Source.Pack!.File.StartsWith("records/", StringComparison.Ordinal) && r.Source.Pack.Line > 0);
        back.Problems().Should().BeEmpty();
    }

    [Fact]
    public void A_snapshot_pack_without_a_commit_takes_its_version_as_the_source()
    {
        var source = new Dictionary<string, byte[]>(PackAuthoring.FromSnapshot(CodeMap("git:local"), "io.example.acme-map", "2.1.0"), StringComparer.Ordinal);

        PackLoader.Load(PackAuthoring.Build(source, _key.PrivateKeyPem), Trust()).SnapshotSource.Should().Be("pack:2.1.0");
    }

    [Fact]
    public void A_snapshot_is_refused_for_another_definition_or_a_version_outside_its_range()
    {
        var loaded = PackLoader.Load(PackAuthoring.Build(Source(CodeMap("git:local")), _key.PrivateKeyPem), Trust());

        FluentActions.Invoking(() => loaded.ToSnapshot(WorkPack.Manifest)).Should().Throw<PackLoadException>()
            .Which.Problems.Should().ContainSingle().Which.Should().Contain($"not of {WorkPack.Id}");
        var later = CodeMapPack.Manifest with { Version = ModuleVersion.Parse("0.2.0") };
        FluentActions.Invoking(() => loaded.ToSnapshot(later)).Should().Throw<PackLoadException>()
            .Which.Problems.Should().ContainSingle().Which.Should().Contain("the host has 0.2.0");
    }

    [Fact]
    public void A_record_its_definition_does_not_admit_is_refused_with_its_file_and_line_before_any_import()
    {
        var source = Source(CodeMap("git:local"));
        var file = source.Keys.First(k => k.StartsWith("records/", StringComparison.Ordinal));
        var lines = Encoding.UTF8.GetString(source[file]).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        source[file] = [.. source[file], .. "{\"type\":\"CodeMapNothing\",\"key\":\"x\",\"properties\":{}}\n"u8.ToArray()];

        var loaded = PackLoader.Load(PackAuthoring.Build(source, _key.PrivateKeyPem), Trust());

        FluentActions.Invoking(() => loaded.ToSnapshot(CodeMapPack.Manifest)).Should().Throw<PackLoadException>()
            .Which.Problems.Should().Contain(p => p.Contains($"{file}:{lines + 1}", StringComparison.Ordinal));
    }

    [Fact]
    public void A_snapshot_pack_is_not_composed_as_a_module()
    {
        var loaded = PackLoader.Load(PackAuthoring.Build(Source(CodeMap("git:local")), _key.PrivateKeyPem), Trust());

        FluentActions.Invoking(() => new ModuleSetBuilder().AddPack(loaded)).Should().Throw<PackLoadException>()
            .Which.Problems.Single().Should().Contain("compose its definition");
    }

    [Theory]
    [InlineData("""{"commit": "not-a-commit"}""", "source.commit")]
    [InlineData("""{"branch": "main"}""", "malformed 'source'")]
    [InlineData("""{"builtAt": "yesterday"}""", "source.builtAt")]
    public void A_snapshot_pack_with_a_malformed_source_is_refused(string field, string expected)
    {
        var source = Source(CodeMap("git:local"));
        var pack = JsonNode.Parse(source["pack.json"])!.AsObject();
        pack["source"] = JsonNode.Parse(field);
        source["pack.json"] = Encoding.UTF8.GetBytes(pack.ToJsonString());

        FluentActions.Invoking(() => PackLoader.Load(PackAuthoring.Build(source, _key.PrivateKeyPem), Trust())).Should().Throw<PackLoadException>()
            .Which.Problems.Should().Contain(p => p.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void A_composite_does_not_contain_a_snapshot()
    {
        var snapshot = PackAuthoring.Build(Source(CodeMap("git:local")), _key.PrivateKeyPem);
        var composite = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["pack.json"] = """{"format":"agentmemory.pack/1.2","id":"io.example.family","version":"1.0.0","kind":"composite","contains":["io.example.acme-map"]}"""u8.ToArray(),
            ["members.json"] = """{"members":[{"id":"io.example.acme-map","version":"[1.0,2.0)","priority":10}]}"""u8.ToArray(),
        };
        var root = Directory.CreateTempSubdirectory("snapshot-composite-").FullName;
        try
        {
            var compositePath = Path.Combine(root, "family.ampack");
            var memberPath = Path.Combine(root, "map.ampack");
            File.WriteAllBytes(compositePath, PackAuthoring.BuildComposite(composite, [snapshot], _key.PrivateKeyPem, Trust()));
            File.WriteAllBytes(memberPath, snapshot);

            FluentActions.Invoking(() => PackFamilies.LoadComposite(compositePath, [memberPath], Trust())).Should().Throw<PackLoadException>()
                .Which.Problems.Should().Contain(p => p.Contains("carry it beside io.example.family in a pack set", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
