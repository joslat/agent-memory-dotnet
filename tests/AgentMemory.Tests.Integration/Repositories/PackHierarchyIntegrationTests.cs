#pragma warning disable AMEXT001
using System.Text;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Packs;
using FluentAssertions;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// AMPACK-BATTLE-TEST, step 4 (24 Nov 2026): a hierarchy of families three composites deep on a real Neo4j (arts › styles ›
/// karate › schools, with judo beside karate), installed as one set: recalled, a school and then a whole branch switched off
/// by their paths (R-HOST-068), upgraded as one unit to a version without one school (only that school released), and
/// uninstalled whole.
/// </summary>
public sealed partial class ModuleRecordWriteIntegrationTests
{
    private const string TreeShotokan = "io.example.tree.shotokan";
    private const string TreeKyokushin = "io.example.tree.kyokushin";
    private const string TreeJudo = "io.example.tree.judo";
    private const string TreeKarate = "io.example.tree.karate";
    private const string TreeStyles = "io.example.tree.styles";
    private const string TreeArts = "io.example.tree.arts";

    private static byte[] TreeComposite(string id, string version, IEnumerable<string> memberIds, IEnumerable<byte[]> members) =>
        PackAuthoring.BuildComposite(PackAuthoring.FromModuleBundle(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["manifest.json"] = Encoding.UTF8.GetBytes(
                $$"""{"format":"agentmemory.composite/v1alpha1","id":"{{id}}","version":"{{version}}","members":[""" +
                string.Join(",", memberIds.Select(m => $$"""{"id":"{{m}}","version":"[1,2)"}""")) + "]}"),
        }), members, AmpackKey.PrivateKeyPem, AmpackTrust());

    // The whole tree as one set at a version; without Kyokushin when asked, every composite above it a new version too.
    private static byte[] TreeSet(string version, byte[] shotokan, byte[] kyokushin, byte[] judo, bool withKyokushin = true)
    {
        var schools = withKyokushin ? new[] { (TreeShotokan, shotokan), (TreeKyokushin, kyokushin) } : new[] { (TreeShotokan, shotokan) };
        var karate = TreeComposite(TreeKarate, version, schools.Select(s => s.Item1), schools.Select(s => s.Item2));
        var styles = TreeComposite(TreeStyles, version, [TreeKarate, TreeJudo], [karate, judo]);
        var arts = TreeComposite(TreeArts, version, [TreeStyles], [styles]);
        return PackAuthoring.BuildSet(TreeArts + "-set", version, [arts, styles, karate, .. schools.Select(s => s.Item2), judo], AmpackKey.PrivateKeyPem, AmpackTrust());
    }

    [Fact]
    public async Task AFamilyThreeCompositesDeepIsRecalledSwitchedOffByPathUpgradedAsOneUnitAndUninstalled()
    {
        var shotokan = DojoMember(TreeShotokan, "TreeShotokan", "TREE_SHOTOKAN", "1.0.0");
        var kyokushin = DojoMember(TreeKyokushin, "TreeKyokushin", "TREE_KYOKUSHIN", "1.0.0");
        var judo = DojoMember(TreeJudo, "TreeJudo", "TREE_JUDO", "1.0.0");
        var root = Directory.CreateTempSubdirectory("pack-tree-neo4j-").FullName;
        var importer = new KnowledgePackImporter(new Neo4jKnowledgePackStore(fixture.TransactionRunner));
        var question = new ContextRequest { SessionId = "s", Owner = "lea", Query = "Which kata do I learn for 8th kyu in Shotokan? Heian Shodan" };
        var leaves = new[] { TreeShotokan, TreeKyokushin, TreeJudo };
        try
        {
            var store = PackStore.Open(root);
            store.Install(TreeSet("1.0.0", shotokan, kyokushin, judo), AmpackTrust()).Members.Should().BeEquivalentTo(leaves);
            var set = PackFamilies.LoadSet(store.Archives().Single(), AmpackTrust());
            var arts = set.Families.Should().ContainSingle().Subject;
            arts.AllMembers.Select(m => m.Id).Should().BeEquivalentTo(leaves);
            foreach (var leaf in arts.AllMembers) (await importer.ImportAsync(leaf.Knowledge!)).Created.Should().Be(93, leaf.Id);

            // The whole tree recalled; one school switched off by its path; then the whole karate branch.
            await using (var host = Host(m => m.UseNeo4jStore().AddFamily(arts)))
            {
                var recalled = await CompileAsync(host, question);
                leaves.Count(id => OffersFrom(recalled, id)).Should().BeGreaterThan(0);
            }
            var shotokanPath = string.Join(CompositeOptions.PathSeparator, TreeStyles, TreeKarate, TreeShotokan);
            await using (var host = Host(m => m.UseNeo4jStore().AddFamily(arts, o => o.Disable(shotokanPath))))
            {
                var recalled = await CompileAsync(host, question);
                OffersFrom(recalled, TreeShotokan).Should().BeFalse("switched off by its path, three composites down");
                (OffersFrom(recalled, TreeKyokushin) || OffersFrom(recalled, TreeJudo)).Should().BeTrue("the rest of the tree still answers");
            }
            await using (var host = Host(m => m.UseNeo4jStore().AddFamily(arts, o => o.Disable(TreeStyles + CompositeOptions.PathSeparator + TreeKarate))))
            {
                var recalled = await CompileAsync(host, question);
                OffersFrom(recalled, TreeShotokan).Should().BeFalse("the karate branch is off");
                OffersFrom(recalled, TreeKyokushin).Should().BeFalse("the karate branch is off");
                OffersFrom(recalled, TreeJudo).Should().BeTrue("judo, beside karate, still answers");
            }

            // Upgraded as one unit: a newer tree without Kyokushin releases exactly that school; the rest is unchanged.
            var upgraded = store.Install(TreeSet("1.1.0", shotokan, kyokushin, judo, withKyokushin: false), AmpackTrust());
            upgraded.Released.Should().Equal(TreeKyokushin);
            foreach (var id in upgraded.Released) await importer.RemoveByIdAsync(id);
            var newer = PackFamilies.LoadSet(store.Archives().Single(), AmpackTrust()).Families.Single();
            foreach (var leaf in newer.AllMembers) (await importer.ImportAsync(leaf.Knowledge!)).Unchanged.Should().Be(93, leaf.Id);
            (await LiveOfPackAsync(TreeKyokushin)).Should().Be(0);
            (await LiveOfPackAsync(TreeShotokan)).Should().Be(93);

            // Uninstalled whole: every school it held let go, nothing live.
            store.Uninstall(TreeArts + "-set", out var released).Should().BeTrue();
            released.Should().Equal(TreeJudo, TreeShotokan);
            foreach (var id in released) await importer.RemoveByIdAsync(id);
            foreach (var id in leaves) (await LiveOfPackAsync(id)).Should().Be(0, id);
        }
        finally
        {
            foreach (var id in leaves) await importer.RemoveByIdAsync(id);
            Directory.Delete(root, recursive: true);
        }
    }
}
