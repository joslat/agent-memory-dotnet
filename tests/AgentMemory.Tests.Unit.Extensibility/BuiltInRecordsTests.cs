#pragma warning disable AMEXT001
using System.Text;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// Feature 03: a pack may hold records of the built-in types (<c>note</c>, <c>chunk</c>, <c>document</c>) without declaring
/// an ontology, and AgentMemory binds them under the module's prefix, in one place, so every path composes the same module
/// (spec/host/agentmemory-binding.md).
/// </summary>
public sealed class BuiltInRecordsTests
{
    private static readonly PackSigningKey Key = PackAuthoring.CreateKey();
    private const string Id = "com.example.lantern-notes";

    private static LoadedPack Load(string records, string? ontology = null)
    {
        var module = """{"format":"agentmemory.module/v1alpha1","id":"com.example.lantern-notes","version":"1.0.0","sdk":"[0.1,0.2)","seed":["records/notes.jsonl"]}""";
        var bundle = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["manifest.json"] = Encoding.UTF8.GetBytes(module), ["records/notes.jsonl"] = Encoding.UTF8.GetBytes(records),
        };
        var source = new Dictionary<string, byte[]>(PackAuthoring.FromModuleBundle(bundle), StringComparer.Ordinal);
        if (ontology is not null) source["ontology.json"] = Encoding.UTF8.GetBytes(ontology);
        var trust = PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(Key.PublicKeyPem, "com.example")));
        return PackLoader.Load(PackAuthoring.Build(source, Key.PrivateKeyPem), trust);
    }

    [Fact]
    public void A_pack_of_notes_needs_no_ontology_and_is_stored_under_its_implied_prefix()
    {
        var pack = Load("""
            {"type":"note","key":"n1","properties":{"text":"The lantern is lit at dusk."}}
            {"type":"note","key":"n2","properties":{"text":"The lantern is put out at dawn."}}

            """.Replace("\r\n", "\n", StringComparison.Ordinal));

        pack.Manifest.Ontology!.LabelPrefix.Should().Be("LanternNotes", "the last segment of the pack's id, in PascalCase");
        pack.Manifest.Ontology.Types.Should().ContainSingle().Which.Should().Match<ModuleType>(t => t.Name == "LanternNotesNote" && t.BuiltIn == "note" && t.Text == "text" && t.Scope == "shared");
        pack.Knowledge!.Proposals.Select(p => p.Type).Should().AllBe("LanternNotesNote");
    }

    [Fact]
    public void Chunks_of_a_document_are_linked_to_it_and_ordered_by_their_index()
    {
        var pack = Load("""
            {"type":"document","key":"guide","properties":{"title":"The lantern guide","uri":"https://example.com/guide"}}
            {"type":"chunk","key":"guide#1","properties":{"text":"Light it at dusk.","index":1},"links":[{"relation":"part_of","target":"guide"}]}
            {"type":"chunk","key":"guide#2","properties":{"text":"Put it out at dawn.","index":2},"links":[{"relation":"part_of","target":"guide"}]}

            """.Replace("\r\n", "\n", StringComparison.Ordinal));

        // No "next" link: a chunk's index orders it, and a knowledge pack's relations join two different types.
        pack.Manifest.Ontology!.Relations.Select(r => (r.Name, r.BuiltIn, r.From, r.To))
            .Should().Equal(("LANTERN_NOTES_PART_OF", (string?)"part_of", "LanternNotesChunk", "LanternNotesDocument"));
        pack.Knowledge!.Proposals.Single(p => p.Key == "guide#1").Links.Select(l => l.Relation).Should().Equal("LANTERN_NOTES_PART_OF");
    }

    [Fact]
    public void A_built_in_record_holds_only_its_properties()
    {
        FluentActions.Invoking(() => Load("""{"type":"note","key":"n1","properties":{"text":"x","colour":"red"}}""" + "\n"))
            .Should().Throw<Exception>().Where(e => e.ToString().Contains("colour", StringComparison.Ordinal));
        FluentActions.Invoking(() => Load("""{"type":"chunk","key":"c1","properties":{"text":"x"}}""" + "\n"))
            .Should().Throw<Exception>("a chunk's index is required").Where(e => e.ToString().Contains("index", StringComparison.Ordinal));
    }

    [Fact]
    public void A_pack_s_own_ontology_may_not_carry_the_host_s_marker()
    {
        var ontology = """{"labelPrefix":"Lantern","types":[{"name":"LanternNote","scope":"shared","builtIn":"note","properties":{"text":{"type":"string"}}}]}""";

        FluentActions.Invoking(() => Load("""{"type":"LanternNote","key":"n1","properties":{"text":"x"}}""" + "\n", ontology))
            .Should().Throw<Exception>().Where(e => e.ToString().Contains("builtIn", StringComparison.Ordinal));
    }

    [Fact]
    public void Built_in_records_sit_beside_declared_types_under_the_declared_prefix()
    {
        var ontology = """{"labelPrefix":"Lantern","types":[{"name":"LanternRule","scope":"shared","text":"rule","memoryKind":"procedural","properties":{"rule":{"type":"string","required":true}}}]}""";

        var pack = Load("""
            {"type":"LanternRule","key":"r1","properties":{"rule":"Never leave it lit."}}
            {"type":"note","key":"n1","properties":{"text":"It was lit on Tuesday."}}

            """.Replace("\r\n", "\n", StringComparison.Ordinal), ontology);

        pack.Manifest.Ontology!.Types.Select(t => (t.Name, t.MemoryKind, t.BuiltIn)).Should().Equal(("LanternRule", "procedural", null), ("LanternNote", null, "note"));
        pack.Knowledge!.Proposals.Select(p => p.Type).Should().BeEquivalentTo(["LanternRule", "LanternNote"]);
    }

    [Theory]
    [InlineData(""","text":"rule","memoryKind":"declarative" """, "memoryKind")]
    [InlineData(""","text":"count" """, ".text")]
    [InlineData(""","text":"missing" """, ".text")]
    public void A_type_s_main_text_is_a_string_property_and_its_kind_one_of_the_registry_s(string extra, string where)
    {
        var json = """{"format":"agentmemory.module/v1alpha1","id":"com.example.rules","version":"1.0.0","sdk":"[0.1,0.2)","ontology":{"labelPrefix":"Rules","types":[{"name":"RulesItem","scope":"shared" """.TrimEnd()
            + extra.TrimEnd() + ""","properties":{"rule":{"type":"string"},"count":{"type":"int"}}}]}}""";

        ModuleManifest.Parse(json).Validate().Should().Contain(e => e.Contains(where, StringComparison.Ordinal));
    }
}
