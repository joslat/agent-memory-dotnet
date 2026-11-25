#pragma warning disable AMEXT001
using System.Text;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j;
using AgentMemory.Extensibility.Neo4j.Migrations;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Packs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// AMPACK-BATTLE-TEST, step 3 (24 Nov 2026): two Mind sets that carry the same values and relationship packs, as every Mind
/// set does, installed side by side, composed by one host on a real Neo4j, and uninstalled one at a time (R-HOST-067): the
/// first set to go lets go only of what is its own, so the values stay live for the other Mind; the last lets go of them.
/// </summary>
public sealed partial class ModuleRecordWriteIntegrationTests
{
    private const string MindCoach = "io.example.minds.coach";
    private const string MindRelationship = "io.example.minds.relationship";

    // A Mind family locked to exactly these members: the shared values and relationship packs and the Mind's own behaviour.
    private static byte[] MindFamilyOf(string mind, byte[] values, byte[] behaviour, byte[] relationship) =>
        PackAuthoring.BuildComposite(PackAuthoring.FromModuleBundle(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["manifest.json"] = Encoding.UTF8.GetBytes($$"""
                {"format":"agentmemory.composite/v1alpha1","id":"io.example.minds.family.{{mind}}","version":"1.0.0",
                 "members":[{"id":"{{MindValues}}","version":"[1,2)"},{"id":"io.example.minds.{{mind}}","version":"[1,2)"},{"id":"{{MindRelationship}}","version":"[1,2)"}]}
                """),
        }), [values, behaviour, relationship], AmpackKey.PrivateKeyPem, AmpackTrust());

    [Fact]
    public async Task TwoMindSetsSharingTheirValuesHoldThemTogetherAndOnlyTheLastSetToGoClosesTheirRecords()
    {
        var (values, relationship, tutor, coach) = (MindMember("values"), MindMember("relationship"), MindMember("tutor"), MindMember("coach"));
        var tutorSet = PackAuthoring.BuildSet("io.example.minds.tutor-set", "1.0.0",
            [MindFamilyOf("tutor", values, tutor, relationship), values, tutor, relationship, WaterCycleCorpus()], AmpackKey.PrivateKeyPem, AmpackTrust());
        var coachSet = PackAuthoring.BuildSet("io.example.minds.coach-set", "1.0.0",
            [MindFamilyOf("coach", values, coach, relationship), values, coach, relationship], AmpackKey.PrivateKeyPem, AmpackTrust());
        var root = Directory.CreateTempSubdirectory("pack-holding-neo4j-").FullName;
        var importer = new KnowledgePackImporter(new Neo4jKnowledgePackStore(fixture.TransactionRunner));
        var question = new ContextRequest { SessionId = "s", Owner = "lea", Query = "Explain the water cycle: what happens during evaporation and condensation?" };
        try
        {
            // Installed side by side: each set holds the values and the relationship.
            var store = PackStore.Open(root);
            store.Install(tutorSet, AmpackTrust());
            store.Install(coachSet, AmpackTrust());
            store.Holders(MindValues).Should().Equal("io.example.minds.coach-set", "io.example.minds.tutor-set");
            store.Holders(MindRelationship).Should().Equal("io.example.minds.coach-set", "io.example.minds.tutor-set");

            // One host composes both Minds: the shared packs once. Everything held with records is imported, each once.
            var sets = store.Archives().Select(archive => PackFamilies.LoadSet(archive, AmpackTrust())).ToList();
            var held = PackFamilies.HostComposes(sets, []);
            held.Count(p => p.Id == MindValues).Should().Be(1);
            foreach (var pack in held.Where(p => p.Knowledge is not null)) await importer.ImportAsync(pack.Knowledge!);
            await using (var host = Host(m =>
            {
                m.UseNeo4jStore();
                foreach (var set in sets)
                {
                    foreach (var family in set.Families) m.AddFamily(family);
                    foreach (var pack in set.Packs) m.AddPack(pack);
                }
            }))
            {
                // The definition member (relationship) has no records to import: its schema comes with the composed set.
                await using (var scope = host.CreateAsyncScope())
                    (await new ModuleMigrator(fixture.TransactionRunner, scope.ServiceProvider.GetRequiredService<ModuleSet>().Resolution).ApplyAsync("two minds"))
                        .Problems.Should().BeEmpty();
                var recalled = await CompileAsync(host, question);
                OffersFrom(recalled, MindCorpus).Should().BeTrue("the tutor's corpus answers; omitted: " + string.Join("; ", recalled.Omissions.Select(o => o.Reason + " " + o.Detail)));
                foreach (var set in sets)
                    MindKnowledge.Bind(MindReader.Read(set.Families.Single()), set.Packs, DateTimeOffset.UtcNow).IsComplete.Should().BeTrue(set.Id);
            }

            // The tutor's set goes first: it lets go of its own Mind and corpus, not of what the coach's set still holds.
            store.Uninstall("io.example.minds.tutor-set", out var released).Should().BeTrue();
            released.Should().Equal(MindCorpus, MindTutor);
            foreach (var id in released) await importer.RemoveByIdAsync(id);
            (await LiveOfPackAsync(MindTutor)).Should().Be(0);
            (await LiveOfPackAsync(MindCorpus)).Should().Be(0);
            (await LiveOfPackAsync(MindValues)).Should().BeGreaterThan(0, "the coach's set still holds the values");
            (await LiveOfPackAsync(MindCoach)).Should().BeGreaterThan(0);

            // The last set to hold them lets go of everything.
            store.Uninstall("io.example.minds.coach-set", out released).Should().BeTrue();
            released.Should().Equal(MindCoach, MindRelationship, MindValues);
            foreach (var id in released) await importer.RemoveByIdAsync(id);
            foreach (var id in new[] { MindValues, MindTutor, MindCoach, MindCorpus }) (await LiveOfPackAsync(id)).Should().Be(0, id);
        }
        finally
        {
            foreach (var id in new[] { MindValues, MindTutor, MindCoach, MindCorpus }) await importer.RemoveByIdAsync(id);
            Directory.Delete(root, recursive: true);
        }
    }
}
