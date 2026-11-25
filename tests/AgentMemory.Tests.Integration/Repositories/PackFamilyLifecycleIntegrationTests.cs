#pragma warning disable AMEXT001
using System.Text;
using System.Text.Json.Nodes;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Neo4j;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Packs;
using FluentAssertions;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// AMPACK-BATTLE-TEST, step 1 (24 Nov 2026): a family of two knowledge packs with ontologies, carried in a pack set, through
/// the lifecycle on a real Neo4j: loaded and recalled through a host that composes the family, a newer family whose second
/// member dropped a type and two relations from its ontology, removed whole, and loaded again.
/// </summary>
public sealed partial class ModuleRecordWriteIntegrationTests
{
    private const string FamilyId = "io.example.dojofamily";
    private const string FamilyOne = "io.example.dojoone";
    private const string FamilyTwo = "io.example.dojotwo";

    // The real martial arts pack under another id and other names, at a version; without techniques when asked: the type, its
    // view, its seed, its two relations and every kata's link to a technique gone, as a publisher's newer version would drop them.
    private static byte[] DojoMember(string id, string name, string relation, string version, bool withoutTechniques = false)
    {
        var bundle = RealBundle("MartialArts").ToDictionary(file => file.Key, file => file.Key == "manifest.json" || file.Key.EndsWith(".jsonl", StringComparison.Ordinal)
            ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(file.Value).Replace("MartialArts", name, StringComparison.Ordinal)
                .Replace("MARTIAL_ARTS", relation, StringComparison.Ordinal))
            : file.Value, StringComparer.Ordinal);
        var manifest = JsonNode.Parse(bundle["manifest.json"])!.AsObject();
        manifest["id"] = id;
        manifest["version"] = version;
        if (withoutTechniques)
        {
            var ontology = manifest["ontology"]!.AsObject();
            var types = ontology["types"]!.AsArray();
            types.Remove(types.Single(t => t!["name"]!.GetValue<string>() == name + "Technique"));
            var relations = ontology["relations"]!.AsArray();
            foreach (var gone in relations.Where(r => r!["from"]!.GetValue<string>() == name + "Technique" || r!["to"]!.GetValue<string>() == name + "Technique").ToList())
                relations.Remove(gone);
            var views = manifest["views"]!.AsArray();
            views.Remove(views.Single(v => v!["type"]!.GetValue<string>() == name + "Technique"));
            var seed = manifest["seed"]!.AsArray();
            seed.Remove(seed.Single(s => s!.GetValue<string>() == "seed/techniques.jsonl"));
            bundle.Remove("seed/techniques.jsonl");
            var uses = relation + "_KATA_USES";
            bundle["seed/kata.jsonl"] = Encoding.UTF8.GetBytes(string.Concat(Encoding.UTF8.GetString(bundle["seed/kata.jsonl"])
                .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
                {
                    var record = JsonNode.Parse(line)!.AsObject();
                    if (record["links"] is JsonArray links)
                        foreach (var link in links.Where(l => l!["relation"]!.GetValue<string>() == uses).ToList()) links.Remove(link);
                    return record.ToJsonString() + "\n";
                })));
        }
        bundle["manifest.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        return PackAuthoring.Build(PackAuthoring.FromModuleBundle(bundle), AmpackKey.PrivateKeyPem);
    }

    // The two members locked into one family, carried with it in one pack set.
    private static LoadedFamily DojoFamily(string version, byte[] one, byte[] two)
    {
        var composite = PackAuthoring.BuildComposite(PackAuthoring.FromModuleBundle(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["manifest.json"] = Encoding.UTF8.GetBytes($$"""
                {"format":"agentmemory.composite/v1alpha1","id":"{{FamilyId}}","version":"{{version}}",
                 "members":[{"id":"{{FamilyOne}}","version":"[1,2)"},{"id":"{{FamilyTwo}}","version":"[1,2)"}]}
                """),
        }), [one, two], AmpackKey.PrivateKeyPem, AmpackTrust());
        var set = PackFamilies.LoadSet(PackAuthoring.BuildSet(FamilyId + "-set", version, [composite, one, two], AmpackKey.PrivateKeyPem, AmpackTrust()), AmpackTrust());
        set.Packs.Should().BeEmpty("both packs are the family's members");
        return set.Families.Should().ContainSingle().Subject;
    }

    private static bool OffersFrom(ContextEnvelope envelope, string packId) =>
        envelope.Sections.Any(s => s.Items.Any(i => i.Provenance?.RecordModuleId == packId));

    [Fact]
    public async Task AFamilyOfTwoKnowledgePacksIsRecalledUpgradesAMemberWhoseOntologyDroppedATypeAndIsRemovedAndLoadedAgain()
    {
        var importer = new KnowledgePackImporter(new Neo4jKnowledgePackStore(fixture.TransactionRunner));
        var one = DojoMember(FamilyOne, "DojoOne", "DOJO_ONE", "1.0.0");
        var family10 = DojoFamily("1.0.0", one, DojoMember(FamilyTwo, "DojoTwo", "DOJO_TWO", "1.0.0"));
        var family11 = DojoFamily("1.1.0", one, DojoMember(FamilyTwo, "DojoTwo", "DOJO_TWO", "1.1.0", withoutTechniques: true));
        var question = new ContextRequest { SessionId = "s", Owner = "lea", Query = "Which kata do I learn for 8th kyu in Shotokan? Heian Shodan" };
        try
        {
            // Loaded: each member imported, and a host that composes the family recalls them.
            foreach (var member in family10.AllMembers) (await importer.ImportAsync(member.Knowledge!)).Created.Should().Be(93, member.Id);
            await using (var host = Host(m => m.UseNeo4jStore().AddFamily(family10)))
            {
                var recalled = await CompileAsync(host, question);
                (OffersFrom(recalled, FamilyOne) || OffersFrom(recalled, FamilyTwo)).Should().BeTrue("the family's kata answer the question");
            }

            // A newer family: the first member unchanged, the second without techniques. Its drops are seen before the import.
            var twoNewer = family11.AllMembers.Single(m => m.Id == FamilyTwo);
            var preview = await importer.DropsAsync(twoNewer.Knowledge!);
            preview.Types.Should().Equal("DojoTwoTechnique");
            preview.Relations.Should().Equal("DOJO_TWO_KATA_USES", "DOJO_TWO_TECHNIQUE_OF");
            (await importer.ImportAsync(family11.AllMembers.Single(m => m.Id == FamilyOne).Knowledge!)).Unchanged.Should().Be(93);
            var upgraded = await importer.ImportAsync(twoNewer.Knowledge!);
            upgraded.Dropped.Types.Should().Equal("DojoTwoTechnique");
            upgraded.Closed.Should().Be(54, "every technique");
            (await PackRecordsAsync("DojoTwoTechnique", FamilyTwo)).Live.Should().Be(0);
            (await Rows($"MATCH (k:DojoTwoKata)-[r:DOJO_TWO_KATA_USES]->() WHERE k.invalidated_at IS NULL RETURN count(r) AS n")).Single()["n"]
                .Should().Be(0L, "no live kata keeps a link to a technique");
            (await PackRecordsAsync("DojoOneTechnique", FamilyOne)).Live.Should().Be(54, "the other member keeps its own");
            await using (var host = Host(m => m.UseNeo4jStore().AddFamily(family11)))
            {
                var recalled = await CompileAsync(host, question);
                (OffersFrom(recalled, FamilyOne) || OffersFrom(recalled, FamilyTwo)).Should().BeTrue("the newer family still answers");
            }

            // Removed whole: nothing of either member is live, and the host recalls nothing of them.
            foreach (var member in family11.AllMembers) await importer.RemoveAsync(member.Manifest);
            (await LiveOfPackAsync(FamilyOne)).Should().Be(0);
            (await LiveOfPackAsync(FamilyTwo)).Should().Be(0);
            await using (var host = Host(m => m.UseNeo4jStore().AddFamily(family11)))
            {
                var recalled = await CompileAsync(host, question);
                OffersFrom(recalled, FamilyOne).Should().BeFalse();
                OffersFrom(recalled, FamilyTwo).Should().BeFalse();
            }

            // Loaded again: every record of the newer family live again, nothing of the dropped type.
            foreach (var member in family11.AllMembers) await importer.ImportAsync(member.Knowledge!);
            (await LiveOfPackAsync(FamilyOne)).Should().Be(93);
            (await LiveOfPackAsync(FamilyTwo)).Should().Be(39, "93 less the 54 techniques");
        }
        finally
        {
            await importer.RemoveByIdAsync(FamilyOne);
            await importer.RemoveByIdAsync(FamilyTwo);
        }
    }
}
