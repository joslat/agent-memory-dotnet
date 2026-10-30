using System.Text;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Packs.CodeMap;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// Hosts load memory packs (pack format v1.2 §4, PACK-host): the existing packs migrate to the v1.2 layout without losing a
/// declaration, a verified .ampack becomes the same knowledge pack the SDK already composes and imports, and a pack the host
/// does not trust, or one that was changed, is refused before any record is read.
/// </summary>
public sealed class AmpackLoaderTests
{
    private static readonly string MartialArts = Path.Combine(AppContext.BaseDirectory, "Packs", "MartialArts");
    private readonly PackKey _key = PackKey.Create();

    private static Dictionary<string, byte[]> Bundle(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes, StringComparer.Ordinal);

    private byte[] Signed(Dictionary<string, byte[]> source)
    {
        var (files, _) = PackBuilder.Build(source);
        PackBuilder.Sign(files, _key.PrivateKeyPem, "publisher", DateTimeOffset.UtcNow.AddMinutes(-1));
        return PackBuilder.Pack(files);
    }

    private PackTrust Trust(string prefix = "io.agentmemory") => PackTrust.Parse(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new
    {
        format = "agentmemory.pack.trust/1.1",
        keys = new[] { new { keyId = _key.KeyId, publicKey = _key.PublicKeyPem, idPrefixes = new[] { prefix }, roles = new[] { "publisher" } } },
    })));

    [Fact]
    public void The_martial_arts_pack_migrates_and_loads_as_the_same_knowledge_pack()
    {
        var bundle = Bundle(MartialArts);
        var original = ModuleManifest.Parse(Encoding.UTF8.GetString(bundle["manifest.json"]));

        var source = PackLayout.FromModule(bundle);
        source.Keys.Should().Contain(["pack.json", "ontology.json", "views.json", "records/arts.jsonl", "assets/icon.png"]);
        var loaded = PackLoader.Load(Signed(source), Trust());

        loaded.Kind.Should().Be("knowledge");
        loaded.Knowledge.Should().NotBeNull();
        loaded.Knowledge!.Proposals.Should().HaveCount(bundle.Where(f => f.Key.EndsWith(".jsonl", StringComparison.Ordinal))
            .Sum(f => Encoding.UTF8.GetString(f.Value).Split('\n').Count(l => l.Trim().Length > 0)));
        loaded.Knowledge.Digest.Should().Be(loaded.Digest).And.StartWith("sha256:");
        loaded.Knowledge.Icon.Should().NotBeNull();
        // Everything but the file locations is the same declaration; v1.2 lists record files in path order.
        var moved = original with
        {
            Seed = [.. original.Seed!.Select(s => "records/" + s[(s.LastIndexOf('/') + 1)..]).Order(StringComparer.Ordinal)],
            Display = original.Display! with { Icon = "assets/icon.png" },
        };
        PackTrialGate.DigestOf(loaded.Manifest).Should().Be(PackTrialGate.DigestOf(moved));
    }

    [Fact]
    public void The_code_map_migrates_as_a_definition_pack_without_losing_a_declaration()
    {
        var manifest = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Packs", "CodeMap", "manifest.json"));
        var source = PackLayout.FromModule(new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["manifest.json"] = manifest });

        source.Keys.Should().BeEquivalentTo(["pack.json", "ontology.json", "traversal.json", "evals/trial.json"]);
        var loaded = PackLoader.Load(Signed(source), Trust());

        loaded.Kind.Should().Be("definition");
        loaded.Knowledge.Should().BeNull();
        PackTrialGate.DigestOf(loaded.Manifest).Should().Be(PackTrialGate.DigestOf(CodeMapPack.Manifest), "the walk, the trial and the consent all survive");
    }

    [Theory]
    [InlineData("Decisions")]
    [InlineData("Work")]
    [InlineData("Repository")]
    public void Every_other_coding_pack_migrates_as_a_definition_pack_without_losing_a_declaration(string pack)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Packs", pack, "manifest.json"));
        var original = ModuleManifest.Parse(Encoding.UTF8.GetString(bytes));

        var loaded = PackLoader.Load(Signed(PackLayout.FromModule(new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["manifest.json"] = bytes })), Trust());

        loaded.Kind.Should().Be("definition");
        PackTrialGate.DigestOf(loaded.Manifest).Should().Be(PackTrialGate.DigestOf(original));
    }

    [Fact]
    public void Review7_W4_packs_are_held_together_as_a_host_holds_them_one_version_of_each()
    {
        // serve and test merged two versions of one pack into one module (a host's builder refuses the second); the same pack
        // reached twice is one pack.
        var source = PackLayout.FromModule(Bundle(MartialArts));
        var first = Signed(source);
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(source["pack.json"])!.AsObject();
        manifest["version"] = "9.9.9";
        var later = Signed(new Dictionary<string, byte[]>(source, StringComparer.Ordinal) { ["pack.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString()) });

        FluentActions.Invoking(() => PackMemory.Mount([first, later], Trust())).Should().Throw<PackLoadException>()
            .Which.Problems.Should().ContainSingle().Which.Should().Contain("comes more than once").And.Contain("a host holds one version of a pack");
        // A host adds a module once (round 11): the same pack given twice is refused, as the host's builder refuses it.
        FluentActions.Invoking(() => PackMemory.Mount([first, first], Trust())).Should().Throw<PackLoadException>()
            .Which.Problems.Should().Contain(p => p.Contains("given more than once"));
    }

    [Fact]
    public void Review8_W3_copies_of_one_pack_are_held_by_one_rule_whatever_their_order()
    {
        // A pack on its own, without an end, and in a set that ends in 30 days: the plan kept the first copy given, so whether the
        // pack expired with the set depended on the order of the arguments. A host adds a module once, so both orders are refused
        // (round 11, R11-1: the copies merged here, and served what a host composing the same content refuses).
        var first = Signed(PackLayout.FromModule(Bundle(MartialArts)));
        var source = PackSets.Source("io.agentmemory.packs.dojo-set", "1.0.0", new Dictionary<string, IReadOnlyDictionary<string, byte[]>>(StringComparer.Ordinal)
        {
            ["io.agentmemory.packs.martialarts"] = StrictZip.Read(first, PackLimits.Default),
        });
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(source["pack.json"])!.AsObject();
        manifest["validUntil"] = DateTimeOffset.UtcNow.AddDays(30).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        source["pack.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        var set = Signed(source);
        LoadedPack Kept(params byte[][] archives) => PackMemory.Mount(archives, Trust()).Packs.Single(p => p.Id == "io.agentmemory.packs.martialarts");

        Kept(set).ValidUntil.Should().NotBeNull("the set alone narrows it to its own end");
        FluentActions.Invoking(() => Kept(first, set)).Should().Throw<PackLoadException>();
        FluentActions.Invoking(() => Kept(set, first)).Should().Throw<PackLoadException>("the same refusal, whatever the order");
    }

    [Fact]
    public void Review11_serve_holds_what_the_host_composes_whatever_the_packaging()
    {
        // R11-1: one module in two sets was served with the later end; a host composing both refuses it. R11-2: a family pinning a
        // member outside the range it declares was served; a host refuses it. Serve runs the host's composition, so it refuses both.
        var pack = Signed(PackLayout.FromModule(Bundle(MartialArts)));
        byte[] SetOf(string id) => Signed(PackSets.Source(id, "1.0.0", new Dictionary<string, IReadOnlyDictionary<string, byte[]>>(StringComparer.Ordinal)
        {
            ["io.agentmemory.packs.martialarts"] = StrictZip.Read(pack, PackLimits.Default),
        }));

        PackMemory.Mount([SetOf("io.agentmemory.packs.set-one")], Trust()).Packs.Should().ContainSingle();
        FluentActions.Invoking(() => PackMemory.Mount([SetOf("io.agentmemory.packs.set-one"), SetOf("io.agentmemory.packs.set-two")], Trust()))
            .Should().Throw<PackLoadException>().Which.Problems.Should().Contain(p => p.Contains("given more than once"));
    }

    [Fact]
    public void A_pack_from_an_untrusted_namespace_is_refused()
    {
        var act = () => PackLoader.Load(Signed(PackLayout.FromModule(Bundle(MartialArts))), Trust(prefix: "io.example"));
        act.Should().Throw<PackLoadException>().Which.Problems.Should().Contain(p => p.Contains("No valid publisher signature"));
    }

    [Fact]
    public void A_changed_record_is_refused_before_any_record_is_read()
    {
        var source = PackLayout.FromModule(Bundle(MartialArts));
        var (files, _) = PackBuilder.Build(source);
        PackBuilder.Sign(files, _key.PrivateKeyPem, "publisher", DateTimeOffset.UtcNow.AddMinutes(-1));
        files["records/arts.jsonl"] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(files["records/arts.jsonl"]).Replace("Japan", "Mars"));

        // The writer refuses to write this archive, so the damaged one is written raw.
        var act = () => PackLoader.Load(StrictZip.Write(files), Trust());

        act.Should().Throw<PackLoadException>().Which.Problems.Should().ContainSingle().Which.Should().Contain("does not match META/files.json");
    }

    [Fact]
    public void A_record_the_ontology_does_not_admit_is_refused_with_its_line()
    {
        var source = PackLayout.FromModule(Bundle(MartialArts));
        source["records/arts.jsonl"] = [.. source["records/arts.jsonl"], .. "{\"type\":\"MartialArtsArt\",\"key\":\"x\",\"properties\":{}}\n"u8.ToArray()];

        var act = () => PackLoader.Load(Signed(source), Trust());

        act.Should().Throw<PackLoadException>().Which.Problems.Should().Contain(p => p.StartsWith("records/arts.jsonl:", StringComparison.Ordinal));
    }

    [Fact]
    public void An_expired_pack_is_refused()
    {
        var source = PackLayout.FromModule(Bundle(MartialArts));
        source["pack.json"] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(source["pack.json"]).Replace("\"tier\"", "\"validUntil\": \"2020-01-01T00:00:00Z\",\n  \"tier\"", StringComparison.Ordinal));

        var act = () => PackLoader.Load(Signed(source), Trust());

        act.Should().Throw<PackLoadException>().Which.Problems.Should().Contain(p => p.Contains("valid until"));
    }
}
