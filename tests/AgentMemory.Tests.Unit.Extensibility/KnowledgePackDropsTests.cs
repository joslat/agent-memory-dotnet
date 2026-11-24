#pragma warning disable AMEXT001
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Packs;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// The host lifecycle (R-HOST-062, AMPACK-BATTLE-TEST, 24 Nov 2026): what a version of a knowledge pack no longer defines
/// of what a store holds live of its id, compared the same way for every store. The Neo4j store's reading of it is proven on a real
/// database (<c>PackOntologyUpgradeIntegrationTests</c>).
/// </summary>
public sealed class KnowledgePackDropsTests
{
    private static readonly ModuleManifest Version11 = ModuleManifest.Parse("""
        {"format":"agentmemory.module/v1alpha1","id":"io.example.packshift","version":"1.1.0","sdk":"[0.1,0.2)",
         "ontology":{"labelPrefix":"PackShift","types":[
           {"name":"PackShiftKata","scope":"shared","properties":{"name":{"type":"string","required":true},"summary":{"type":"string"}}},
           {"name":"PackShiftTechnique","scope":"shared","properties":{"name":{"type":"string","required":true}}}],
           "relations":[{"name":"PACK_SHIFT_TECHNIQUE_IN_KATA","from":"PackShiftTechnique","to":"PackShiftKata"}]}}
        """);

    private static Dictionary<string, IReadOnlyCollection<string>> Live(params (string Type, string[] Properties)[] types) =>
        types.ToDictionary(t => t.Type, t => (IReadOnlyCollection<string>)t.Properties, StringComparer.Ordinal);

    [Fact]
    public void A_type_a_relation_and_a_property_the_version_no_longer_defines_are_named_in_order_once_each()
    {
        var drops = KnowledgePackDrops.Compare(Version11,
            liveTypes: ["PackShiftKata", "PackShiftRank", "PackShiftBelt", "PackShiftRank"],
            liveRelations: ["PACK_SHIFT_KATA_FOR_RANK", "PACK_SHIFT_TECHNIQUE_IN_KATA"],
            Live(("PackShiftKata", ["name", "summary", "belt", "grade"]), ("PackShiftTechnique", ["name"])));

        drops.Types.Should().Equal("PackShiftBelt", "PackShiftRank");
        drops.Relations.Should().Equal("PACK_SHIFT_KATA_FOR_RANK");
        drops.Properties.Should().Equal("PackShiftKata.belt", "PackShiftKata.grade");
        drops.IsEmpty.Should().BeFalse();
        drops.ToString().Should().Be("types PackShiftBelt, PackShiftRank; relations PACK_SHIFT_KATA_FOR_RANK; properties PackShiftKata.belt, PackShiftKata.grade");
    }

    [Fact]
    public void The_sdk_s_own_record_properties_are_never_a_drop()
    {
        var drops = KnowledgePackDrops.Compare(Version11, ["PackShiftKata"], [],
            Live(("PackShiftKata", ["id", "key", "key_id", "created_at", "valid_from", "source_pack", "source_pack_version", "confidence_micro", "name"])));

        drops.Should().BeSameAs(KnowledgePackDrops.None);
    }

    [Fact]
    public void A_type_the_version_drops_is_named_once_and_its_properties_are_not_named_besides()
    {
        // The records of a dropped type are closed whole: naming each of their properties too would say the same twice.
        var drops = KnowledgePackDrops.Compare(Version11, ["PackShiftRank"], [], Live(("PackShiftRank", ["name", "order"])));

        drops.Types.Should().Equal("PackShiftRank");
        drops.Properties.Should().BeEmpty();
    }

    [Fact]
    public void Nothing_dropped_is_the_one_None_so_results_that_drop_nothing_compare_equal()
    {
        KnowledgePackDrops.Compare(Version11, [], [], Live()).Should().BeSameAs(KnowledgePackDrops.None);
        new KnowledgePackImportResult { PackId = "io.example.packshift", Created = 1 }
            .Should().Be(new KnowledgePackImportResult { PackId = "io.example.packshift", Created = 1, Dropped = KnowledgePackDrops.None });
        KnowledgePackDrops.None.ToString().Should().Be("nothing");
    }
}
