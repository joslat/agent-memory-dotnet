#pragma warning disable AMEXT001
using System.Text;
using AgentMemory.Extensibility.Bundles;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Packs;
using FluentAssertions;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// AMPACK-BATTLE-TEST, step 1 (24 Nov 2026): a knowledge pack whose newer version changes its
/// ontology. R-HOST-062: a record the newer version no longer holds is closed, whatever the reason it is gone, and the
/// lifecycle contract decided that the import names what an upgrade drops.
/// </summary>
public sealed partial class ModuleRecordWriteIntegrationTests
{
    private const string ShiftId = "io.example.packshift";

    // 1.0.0: kata (with a belt) for ranks. 1.1.0: ranks and their relation gone, the kata's belt gone, techniques added.
    private const string ShiftManifest10 = """
        {"format":"agentmemory.module/v1alpha1","id":"io.example.packshift","version":"1.0.0","sdk":"[0.1,0.2)",
         "seed":["seed/records.jsonl"],
         "ontology":{"labelPrefix":"PackShift","types":[
           {"name":"PackShiftKata","scope":"shared","render":"{name}: {summary}","properties":{
             "name":{"type":"string","required":true},"summary":{"type":"string"},"belt":{"type":"string"}}},
           {"name":"PackShiftRank","scope":"shared","render":"{name}","properties":{"name":{"type":"string","required":true}}}],
           "relations":[{"name":"PACK_SHIFT_KATA_FOR_RANK","from":"PackShiftKata","to":"PackShiftRank","max":1}]},
         "views":[{"id":"kata","type":"PackShiftKata","search":{"properties":["name","summary"]},"limit":12}]}
        """;

    private const string ShiftManifest11 = """
        {"format":"agentmemory.module/v1alpha1","id":"io.example.packshift","version":"1.1.0","sdk":"[0.1,0.2)",
         "seed":["seed/records.jsonl"],
         "ontology":{"labelPrefix":"PackShift","types":[
           {"name":"PackShiftKata","scope":"shared","render":"{name}: {summary}","properties":{
             "name":{"type":"string","required":true},"summary":{"type":"string"}}},
           {"name":"PackShiftTechnique","scope":"shared","render":"{name}","properties":{"name":{"type":"string","required":true}}}],
           "relations":[]},
         "views":[{"id":"kata","type":"PackShiftKata","search":{"properties":["name","summary"]},"limit":12}]}
        """;

    private static readonly string[] Shift10 =
    [
        """{"type":"PackShiftKata","key":"heian-shodan","properties":{"name":"Heian Shodan","summary":"Down block and lunge punch","belt":"white"},"links":[{"relation":"PACK_SHIFT_KATA_FOR_RANK","target":"8-kyu"}]}""",
        """{"type":"PackShiftKata","key":"heian-nidan","properties":{"name":"Heian Nidan","summary":"Side kick from back stance","belt":"yellow"},"links":[{"relation":"PACK_SHIFT_KATA_FOR_RANK","target":"7-kyu"}]}""",
        """{"type":"PackShiftRank","key":"8-kyu","properties":{"name":"8th kyu"}}""",
        """{"type":"PackShiftRank","key":"7-kyu","properties":{"name":"7th kyu"}}""",
    ];

    private static readonly string[] Shift11 =
    [
        """{"type":"PackShiftKata","key":"heian-shodan","properties":{"name":"Heian Shodan","summary":"Down block and lunge punch"}}""",
        """{"type":"PackShiftKata","key":"heian-nidan","properties":{"name":"Heian Nidan","summary":"Side kick from back stance"}}""",
        """{"type":"PackShiftTechnique","key":"gedan-barai","properties":{"name":"Gedan barai"}}""",
    ];

    private static KnowledgePack ShiftPack(string manifest, string version, string[] seed)
    {
        var content = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [KnowledgePack.ManifestFile] = Encoding.UTF8.GetBytes(manifest),
            ["seed/records.jsonl"] = Encoding.UTF8.GetBytes(string.Join("\n", seed) + "\n"),
        };
        var trust = new BundleTrust();
        trust.DeveloperKeys.Add(PackKey.PublicKeyPem);
        return KnowledgePack.Open(BundleSigning.Sign(content, ShiftId, version, PackKey.PrivateKeyPem), trust);
    }

    private async Task<long> LiveOfPackAsync(string packId) =>
        (long)(await Rows($"MATCH (n) WHERE n.source_pack='{packId}' AND n.invalidated_at IS NULL RETURN count(n) AS live")).Single()["live"];

    [Fact]
    public async Task AnUpgradeThatDropsATypeAPropertyAndARelationClosesWhatItNoLongerHoldsAndARemovalLeavesNothingLive()
    {
        var importer = new KnowledgePackImporter(new Neo4jKnowledgePackStore(fixture.TransactionRunner));
        var v10 = ShiftPack(ShiftManifest10, "1.0.0", Shift10);
        var v11 = ShiftPack(ShiftManifest11, "1.1.0", Shift11);
        try
        {
            (await importer.ImportAsync(v10)).Created.Should().Be(4);

            // Before the upgrade, without writing: what 1.1.0 no longer defines, so a host can refuse it.
            var preview = await importer.DropsAsync(v11);
            preview.Types.Should().Equal("PackShiftRank");
            preview.Relations.Should().Equal("PACK_SHIFT_KATA_FOR_RANK");
            preview.Properties.Should().Equal("PackShiftKata.belt");
            (await LiveOfPackAsync(ShiftId)).Should().Be(4, "a preview writes nothing");

            var upgraded = await importer.ImportAsync(v11);

            upgraded.Dropped.Types.Should().Equal("PackShiftRank");
            upgraded.Dropped.Relations.Should().Equal("PACK_SHIFT_KATA_FOR_RANK");
            upgraded.Dropped.Properties.Should().Equal("PackShiftKata.belt");
            upgraded.Closed.Should().Be(2, "the two ranks");
            (await importer.DropsAsync(v11)).Should().BeSameAs(KnowledgePackDrops.None,
                "once imported, nothing live uses what 1.1.0 does not define; the names stay the id's (R-HOST-060), and are not dropped again");
            (await PackRecordsAsync("PackShiftRank", ShiftId)).Live.Should().Be(0, "1.1.0 holds no rank: every rank is closed (R-HOST-062)");
            var shodan = (await Rows("MATCH (n:PackShiftKata {key:'heian-shodan'}) WHERE n.invalidated_at IS NULL RETURN keys(n) AS keys, "
                + "[(n)-[r:PACK_SHIFT_KATA_FOR_RANK]->() | type(r)] AS links")).Single();
            ValueExtensions.As<List<object>>(shodan["keys"]).Select(k => k.ToString()).Should().NotContain("belt", "1.1.0's kata has no belt");
            ValueExtensions.As<List<object>>(shodan["links"]).Should().BeEmpty("1.1.0 has no relation to a rank");
            (await LiveOfPackAsync(ShiftId)).Should().Be(3, "two kata and one technique");

            await importer.RemoveAsync(v11.Manifest);

            (await LiveOfPackAsync(ShiftId)).Should().Be(0, "a removed pack leaves nothing live, whatever an earlier version held (R-HOST-060)");
        }
        finally { await importer.RemoveByIdAsync(ShiftId); }
    }

    /// <summary>
    /// A store an earlier release upgraded holds live records of a type the newer version no longer defines, under the newer
    /// version's schema; a removal by the newer manifest read only its own types, so those records stayed live (R-HOST-060).
    /// </summary>
    [Fact]
    public async Task ARemovalByANewerManifestClosesTheLiveRecordsOfATypeOnlyAnEarlierVersionDefined()
    {
        var importer = new KnowledgePackImporter(new Neo4jKnowledgePackStore(fixture.TransactionRunner));
        var v10 = ShiftPack(ShiftManifest10, "1.0.0", Shift10);
        var v11 = ShiftPack(ShiftManifest11, "1.1.0", Shift11);
        try
        {
            await importer.ImportAsync(v10);
            var schema = await new AgentMemory.Extensibility.Neo4j.Migrations.ModuleMigrator(fixture.TransactionRunner, v11.Resolution, TimeProvider.System)
                .ApplyAsync("an earlier release's upgrade");
            schema.Problems.Should().BeEmpty();

            (await importer.RemoveAsync(v11.Manifest)).Closed.Should().Be(4, "two kata and the two ranks only 1.0.0 defined");

            (await LiveOfPackAsync(ShiftId)).Should().Be(0);
        }
        finally { await importer.RemoveByIdAsync(ShiftId); }
    }
}
