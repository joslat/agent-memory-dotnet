#pragma warning disable AMEXT001
using System.Text;
using System.Text.Json.Nodes;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Extensibility.Reading;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// Feature 09: what a pack was made from, and a record's confidence capped by where it came from (R-CORE-136, R-CORE-137).
/// </summary>
public sealed class ConfidenceTests
{
    private static readonly PackSigningKey Key = PackAuthoring.CreateKey();
    private const string Derivation = """{"actions":["extract"],"format":"agentmemory.pack.derivation/1","inputs":[{"confidenceMicro":800000,"digest":"sha256:abababababababababababababababababababababababababababababababab","name":"handbook"}],"tool":{"name":"notes-extractor","version":"1.2.0"}}""";

    private static LoadedPack Load(string records, int? signerCap = null, string? derivation = Derivation)
    {
        var module = """{"format":"agentmemory.module/v1alpha1","id":"com.example.lantern-notes","version":"1.0.0","sdk":"[0.1,0.2)","seed":["records/notes.jsonl"]}""";
        var source = new Dictionary<string, byte[]>(PackAuthoring.FromModuleBundle(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["manifest.json"] = Encoding.UTF8.GetBytes(module), ["records/notes.jsonl"] = Encoding.UTF8.GetBytes(records),
        }), StringComparer.Ordinal)
        {
            ["ontology.json"] = Encoding.UTF8.GetBytes("""{"labelPrefix":"Lantern","types":[{"name":"LanternNote","scope":"shared","properties":{"text":{"type":"string"}}}]}"""),
        };
        if (derivation is not null) source["META/derivation.json"] = Encoding.UTF8.GetBytes(derivation);
        var policy = JsonNode.Parse(PackAuthoring.TrustPolicy(Key.PublicKeyPem, "com.example"))!;
        if (signerCap is { } cap) policy["keys"]![0]!["confidenceMicro"] = cap;
        return PackLoader.Load(PackAuthoring.Build(source, Key.PrivateKeyPem), PackTrust.Parse(Encoding.UTF8.GetBytes(policy.ToJsonString())));
    }

    private static int? Confidence(LoadedPack pack, string key) => pack.Knowledge!.Proposals.Single(p => p.Key == key).ConfidenceMicro;

    [Fact]
    public void The_effective_confidence_never_exceeds_any_of_its_parts()
    {
        int?[] parts = [null, 0, 1, 300_000, 800_000, 999_999, ModuleConfidence.Full];
        foreach (var own in parts)
        foreach (var source in parts)
        foreach (var signer in parts)
        {
            var effective = ModuleConfidence.Effective(own, source, signer);
            foreach (var part in new[] { own, source, signer }.OfType<int>()) effective.Should().BeLessThanOrEqualTo(part);
            if (own is null && source is null && signer is null) effective.Should().BeNull("nothing caps a record no part names");
            else effective.Should().Be(new[] { own, source, signer }.OfType<int>().Min());
        }
    }

    [Fact]
    public void A_host_trusts_a_record_no_further_than_its_source_and_its_signer()
    {
        const string Lines = """
            {"type":"LanternNote","key":"own","properties":{"text":"a"},"confidenceMicro":900000,"sourceKey":"handbook"}
            {"type":"LanternNote","key":"bare","properties":{"text":"b"}}
            {"type":"LanternNote","key":"sure","properties":{"text":"c"},"confidenceMicro":500000}

            """;
        var unsigned = Load(Lines);
        Confidence(unsigned, "own").Should().Be(800_000, "the source input is trusted less than the record claims");
        Confidence(unsigned, "bare").Should().BeNull("nothing caps it");
        Confidence(unsigned, "sure").Should().Be(500_000);

        var capped = Load(Lines, signerCap: 700_000);
        Confidence(capped, "own").Should().Be(700_000, "the signer is trusted less still");
        Confidence(capped, "bare").Should().Be(700_000, "a record is trusted no further than who signed it");
        Confidence(capped, "sure").Should().Be(500_000);
    }

    [Fact]
    public void Every_role_the_policy_requires_caps_a_record_through_its_most_trusted_signer()
    {
        // Review F09, M1: the cap was read from publisher signatures only, so a capped curator the policy requires capped nothing.
        var curator = PackAuthoring.CreateKey();
        var policy = JsonNode.Parse(PackAuthoring.TrustPolicy(Key.PublicKeyPem, "com.example"))!;
        var second = JsonNode.Parse(PackAuthoring.TrustPolicy(curator.PublicKeyPem, "com.example"))!["keys"]![0]!.DeepClone();
        second["roles"] = new JsonArray("curator");
        second["confidenceMicro"] = 300_000;
        policy["keys"]!.AsArray().Add(second);
        policy["requireRoles"] = new JsonArray("publisher", "curator");
        var trust = PackTrust.Parse(Encoding.UTF8.GetBytes(policy.ToJsonString()));
        var module = """{"format":"agentmemory.module/v1alpha1","id":"com.example.lantern-notes","version":"1.0.0","sdk":"[0.1,0.2)","seed":["records/notes.jsonl"]}""";
        var source = new Dictionary<string, byte[]>(PackAuthoring.FromModuleBundle(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["manifest.json"] = Encoding.UTF8.GetBytes(module),
            ["records/notes.jsonl"] = Encoding.UTF8.GetBytes("""{"type":"LanternNote","key":"bare","properties":{"text":"b"}}""" + "\n"),
        }), StringComparer.Ordinal)
        {
            ["ontology.json"] = Encoding.UTF8.GetBytes("""{"labelPrefix":"Lantern","types":[{"name":"LanternNote","scope":"shared","properties":{"text":{"type":"string"}}}]}"""),
        };
        var curated = PackAuthoring.Countersign(PackAuthoring.Build(source, Key.PrivateKeyPem), curator.PrivateKeyPem, "curator", trust);
        Confidence(PackLoader.Load(curated, trust), "bare").Should().Be(300_000, "the curator the policy requires is trusted less than the publisher");
    }

    [Fact]
    public void A_source_key_that_names_no_input_refuses_the_pack()
    {
        var lines = """{"type":"LanternNote","key":"own","properties":{"text":"a"},"sourceKey":"elsewhere"}""" + "\n";
        FluentActions.Invoking(() => Load(lines)).Should().Throw<PackLoadException>().Which.Problems.Should().Contain(p => p.Contains("elsewhere"));
        FluentActions.Invoking(() => Load(lines, derivation: null)).Should().Throw<PackLoadException>();
        FluentActions.Invoking(() => Load("""{"type":"LanternNote","key":"x","properties":{"text":"a"},"confidenceMicro":1000001}""" + "\n"))
            .Should().Throw<PackLoadException>();
    }

    [Fact]
    public async Task The_floor_leaves_out_records_below_it_and_the_frame_says_how_far_a_record_is_trusted()
    {
        var reader = new ModuleSet.LiveReader(new Inner(), live: null, TimeProvider.System, minimumConfidence: 750_000);
        (await reader.FindAsync(new() { ModuleId = "m", Type = "T", Limit = 9 }, new())).Records.Select(r => r.Key).Should().Equal("high", "uncapped");

        var provenance = new ContextItemProvenance
        {
            RecordModuleId = "m", RecordType = "T", RecordVersionId = "v", RecordKey = "high", CreatedAtUtc = DateTimeOffset.UnixEpoch,
            SourceSessionId = "pack", ConfidenceMicro = 900_000,
            SourcePack = new PackSource { Id = "com.example.lantern-notes", Version = "1.0.0", Digest = "sha256:0", File = "records/notes.jsonl", Line = 1 },
        };
        ContextFrames.Of(null, provenance).Should().Contain(new KeyValuePair<string, string>("confidence", "0.9"));
        ContextFrames.Of(null, provenance with { ConfidenceMicro = null }).Should().NotContain(a => a.Key == "confidence");
        ModuleConfidence.Shown(ModuleConfidence.Full).Should().Be("1");
        ModuleConfidence.Shown(0).Should().Be("0");
        ModuleConfidence.Shown(123_400).Should().Be("0.1234");
    }

    // A code map whose records sort low-confidence first, so a store that cut before the floor would offer nothing above it.
    private const string Code = """
        {"format":"agentmemory.module/v1alpha1","id":"com.example.code","version":"1.0.0","sdk":"[0.1,0.2)",
         "ontology":{"labelPrefix":"Code","types":[{"name":"CodeFile","scope":"shared","render":"{path}","properties":{"path":{"type":"string","required":true}}}]},
         "traversal":{"anchors":[{"type":"CodeFile","property":"path","match":"exact"}],"maxItems":9}}
        """;

    private static AgentMemory.Extensibility.Ampack.PackMemory Mapped(IEnumerable<(string Path, int? Micro)> files) =>
        AgentMemory.Extensibility.Ampack.PackMemory.From([], [new ModuleSnapshot
        {
            Module = ModuleManifest.Parse(Code), Source = "git:abc",
            Records = [.. files.Select(f => new ModuleProposal
            {
                Kind = ProposalKind.Assert, Type = "CodeFile", Key = f.Path, ConfidenceMicro = f.Micro, Source = new ProposalSource { Excerpt = f.Path },
                Properties = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal) { ["path"] = System.Text.Json.JsonSerializer.SerializeToElement(f.Path) },
            })],
        }]);

    [Fact]
    public async Task A_store_keeps_the_floor_before_its_limit_and_the_host_s_floor_reaches_it()
    {
        // Review F09, H4: the floor was applied after the store's limit, so records below it, ranked first, silenced the rest.
        var memory = Mapped([.. Enumerable.Range(0, 30).Select(i => ($"src/low{i}.cs", (int?)300_000)), ("src/y.cs", null), ("src/z.cs", 900_000)]);
        var query = new ModuleQuery { ModuleId = "com.example.code", Type = "CodeFile", Limit = 2 };
        var scope = new ModuleReadContext { Scope = AgentMemory.Abstractions.Options.MemoryScope.For("pack") };
        (await memory.ReaderFor("com.example.code").FindAsync(query, scope with { MinimumConfidenceMicro = 500_000 })).Records
            .Select(r => r.Key).Should().BeEquivalentTo("src/y.cs", "src/z.cs");
        var hosted = new ModuleSet.LiveReader(memory.ReaderFor("com.example.code"), live: null, TimeProvider.System, minimumConfidence: 500_000);
        (await hosted.FindAsync(query, scope)).Records.Select(r => r.Key).Should().BeEquivalentTo(["src/y.cs", "src/z.cs"], "the host's floor goes to the store with the read");
        (await memory.ReaderFor("com.example.code").FindAsync(query, scope)).Records.Should().HaveCount(2).And.OnlyContain(r => r.ConfidenceMicro == 300_000, "with no floor the store's first two are the low ones");
    }

    [Fact]
    public async Task A_walk_neither_seeds_from_nor_offers_a_record_below_the_floor()
    {
        // Review F09, H3: a walk read around the floor, so a turn's anchors brought back what a view left out.
        var memory = Mapped([("src/a.cs", 300_000), ("src/b.cs", null)]);
        var request = new ContextRequest
        {
            SessionId = "pack", Owner = "pack", Query = "", Anchors = [ContextAnchor.Path("src/a.cs"), ContextAnchor.Path("src/b.cs")],
        };
        (await AgentMemory.Extensibility.Context.Traversal.TraversalWalk.RunAsync(memory.Resolution, memory, request, CancellationToken.None, minimumConfidence: 500_000))
            .Items.Select(i => i.Record.Key).Should().Equal("src/b.cs");
        (await AgentMemory.Extensibility.Context.Traversal.TraversalWalk.RunAsync(memory.Resolution, memory, request, CancellationToken.None))
            .Items.Select(i => i.Record.Key).Should().BeEquivalentTo("src/a.cs", "src/b.cs");
    }

    [Fact]
    public void Only_a_signed_pack_s_records_carry_a_confidence_and_a_source_key()
    {
        // Review F09, H2 and M3: a snapshot's and a bundle's records took them, and nothing capped them or checked the key.
        var source = (int line) => new PackSource { Id = "com.example.code", Version = "git:abc", Digest = "", File = "records/r.jsonl", Line = line };
        foreach (var line in new[] { """{"type":"CodeFile","key":"a","confidenceMicro":900000}""", """{"type":"CodeFile","key":"a","sourceKey":"handbook"}""" })
        {
            var problems = new List<string>();
            KnowledgePack.Lines("records/r.jsonl", Encoding.UTF8.GetBytes(line + "\n"), source, problems).Should().BeEmpty();
            problems.Should().ContainSingle().Which.Should().Contain("only a signed knowledge pack");
        }
        var named = new List<string>();
        KnowledgePack.Lines("records/r.jsonl", Encoding.UTF8.GetBytes("""{"type":"CodeFile","key":"a","sourceKey":"handbook"}""" + "\n"), source, named,
            new HashSet<string>(StringComparer.Ordinal) { "handbook" }).Should().ContainSingle().Which.SourceKey.Should().Be("handbook");
        named.Should().BeEmpty();
    }

    [Fact]
    public void Nothing_but_the_importer_writes_a_confidence_and_a_module_cannot_declare_its_stored_name()
    {
        // Review F09, M2 and L2.
        ModuleManifest.Parse("""
            {"format":"agentmemory.module/v1alpha1","id":"com.example.code","version":"1.0.0","sdk":"[0.1,0.2)",
             "ontology":{"labelPrefix":"Code","types":[{"name":"CodeFile","scope":"shared","properties":{"confidence_micro":{"type":"string"}}}]}}
            """).Validate().Should().Contain(e => e.Contains("reserved SDK property"));
        FluentActions.Invoking(() => ModuleConfidence.Shown(-1)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => ModuleConfidence.Shown(ModuleConfidence.Full + 1)).Should().Throw<ArgumentOutOfRangeException>();
        ModuleConfidence.Clears(null, 1_000_000).Should().BeTrue("a record with no confidence counts as full");
        ModuleConfidence.Clears(499_999, 500_000).Should().BeFalse();
    }

    private sealed class Inner : IModuleReader
    {
        public string CallerModuleId => "m";

        public Task<ModuleReadResult> FindAsync(ModuleQuery query, ModuleReadContext context, CancellationToken cancellationToken = default)
        {
            ModuleRecord Record(string key, int? micro) => new()
            {
                ModuleId = "m", Type = "T", Id = key, Key = key, CreatedAtUtc = DateTimeOffset.UnixEpoch, SourceSessionId = "pack",
                Source = new ProposalSource { Excerpt = key }, ConfidenceMicro = micro,
            };
            return Task.FromResult(new ModuleReadResult { Status = ModuleReadStatus.Succeeded, Records = [Record("low", 700_000), Record("high", 900_000), Record("uncapped", null)] });
        }
    }
}
