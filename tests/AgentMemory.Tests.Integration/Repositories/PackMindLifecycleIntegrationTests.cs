#pragma warning disable AMEXT001
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMemory.Abstractions.Services;
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
/// AMPACK-BATTLE-TEST, step 2 (24 Nov 2026): a Mind (<c>agentmemory.mind/2.0</c>: behaviour, values and relationship members in
/// one family) and the curriculum corpus it needs, in one pack set, on a real Neo4j: read, bound to its knowledge, imported,
/// recalled, its knowledge unloaded, the Mind unloaded, and everything loaded again (the host lifecycle's Mind rules).
/// </summary>
public sealed partial class ModuleRecordWriteIntegrationTests
{
    private const string MindFamilyId = "io.example.minds.family.tutor";
    private const string MindValues = "io.example.minds.values";
    private const string MindTutor = "io.example.minds.tutor";
    private const string MindCorpus = "io.example.minds.corpus";
    private static readonly string MindFixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mind");

    // One Mind member built from the convention's fixtures, as the unit tests build it, through the public authoring API.
    private static byte[] MindMember(string member)
    {
        var contract = JsonNode.Parse(File.ReadAllText(Path.Combine(MindFixtures, member, "mind-contract.json")))!.AsObject();
        var records = File.ReadAllLines(Path.Combine(MindFixtures, member, "records.sample.jsonl")).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject()).ToArray();
        var types = contract["typeRoles"]!.AsObject().Select(p => p.Value!.GetValue<string>()).Distinct().Select(type =>
        {
            var properties = new JsonObject();
            foreach (var record in records.Where(r => r["type"]!.GetValue<string>() == type))
                foreach (var field in record.Where(p => p.Key is not ("type" or "key")))
                    properties[field.Key] = new JsonObject { ["type"] = field.Value is JsonValue value && value.TryGetValue<bool>(out _) ? "bool" : field.Value is JsonValue number && number.TryGetValue<int>(out _) ? "int" : "string" };
            return new JsonObject { ["name"] = type, ["scope"] = "shared", ["properties"] = properties };
        }).ToArray();
        if (member == "relationship") types = [new JsonObject { ["name"] = "EcAdjustment", ["properties"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "string" } } }];
        var ontology = new JsonObject { ["labelPrefix"] = types[0]["name"]!.GetValue<string>()[..2], ["types"] = new JsonArray(types) };
        var source = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["pack.json"] = JsonSerializer.SerializeToUtf8Bytes(new { format = "agentmemory.pack/1.2", id = "io.example.minds." + member, version = "1.0.0", kind = member == "relationship" ? "definition" : "knowledge", sdk = "[0.1,0.2)", containableBy = new[] { "io.example.minds" }, code = "E0", tier = "open", protection = "open" }),
            ["ontology.json"] = Encoding.UTF8.GetBytes(ontology.ToJsonString()),
            ["assets/mind-contract.json"] = Encoding.UTF8.GetBytes(contract.ToJsonString()),
            ["assets/mind-evals.json"] = Encoding.UTF8.GetBytes(File.ReadAllText(Path.Combine(MindFixtures, member, "assets", "mind-evals.json")).ReplaceLineEndings("\n")),
        };
        if (member != "relationship")
            source["records/mind.jsonl"] = Encoding.UTF8.GetBytes(string.Concat(records.Select(record =>
            {
                var props = new JsonObject();
                foreach (var field in record.Where(p => p.Key is not ("type" or "key")))
                {
                    if (field.Value is null) continue;
                    props[field.Key] = field.Value is JsonObject or JsonArray ? JsonValue.Create(field.Value.ToJsonString()) : field.Value.DeepClone();
                }
                return new JsonObject { ["type"] = record["type"]!.DeepClone(), ["key"] = record["key"]!.DeepClone(), ["properties"] = props }.ToJsonString() + "\n";
            })));
        return PackAuthoring.Build(source, AmpackKey.PrivateKeyPem);
    }

    // The curriculum the tutor needs (agentmemory.curriculum/1): an objective, a source and three questions, searchable.
    private static byte[] WaterCycleCorpus()
    {
        var bundle = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["manifest.json"] = Encoding.UTF8.GetBytes("""
                {"format":"agentmemory.module/v1alpha1","id":"io.example.minds.corpus","version":"1.0.0","sdk":"[0.1,0.2)","seed":["seed/corpus.jsonl"],
                 "ontology":{"labelPrefix":"Wc","types":[
                   {"name":"WcObjective","scope":"shared","render":"{text}","properties":{"text":{"type":"string","required":true}}},
                   {"name":"WcSource","scope":"shared","render":"{url}","properties":{"url":{"type":"string","required":true}}},
                   {"name":"WcQuestion","scope":"shared","render":"{stem}","properties":{"stem":{"type":"string","required":true},"correctOption":{"type":"string"},"sourceKey":{"type":"string"},"objectiveKey":{"type":"string"}}}]},
                 "views":[{"id":"objectives","type":"WcObjective","search":{"properties":["text"]},"limit":5},
                          {"id":"questions","type":"WcQuestion","search":{"properties":["stem"]},"limit":5}]}
                """.ReplaceLineEndings("\n")),
            ["seed/corpus.jsonl"] = Encoding.UTF8.GetBytes(string.Concat(new[]
            {
                """{"type":"WcObjective","key":"o1","properties":{"text":"Explain the water cycle: evaporation, condensation and precipitation"}}""",
                """{"type":"WcSource","key":"s1","properties":{"url":"https://example.org/water-cycle"}}""",
                """{"type":"WcQuestion","key":"q1","properties":{"stem":"What happens to water during evaporation?","correctOption":"a","sourceKey":"s1","objectiveKey":"o1"}}""",
                """{"type":"WcQuestion","key":"q2","properties":{"stem":"Where does condensation form clouds?","correctOption":"b","sourceKey":"s1","objectiveKey":"o1"}}""",
                """{"type":"WcQuestion","key":"q3","properties":{"stem":"Which kind of precipitation falls as ice?","correctOption":"c","sourceKey":"s1","objectiveKey":"o1"}}""",
            }.Select(line => line + "\n"))),
        };
        var source = new Dictionary<string, byte[]>(PackAuthoring.FromModuleBundle(bundle), StringComparer.Ordinal)
        {
            ["assets/knowledge-contract.json"] = Encoding.UTF8.GetBytes("""
                {"format":"agentmemory.knowledge/1","interface":"agentmemory.curriculum/1","edition":"water-cycle-2026-11","languages":["en"],"jurisdictions":[],"formats":["mcq-4"],
                 "typeRoles":{"objective":"WcObjective","question":"WcQuestion","source":"WcSource"}}
                """.ReplaceLineEndings("\n")),
        };
        return PackAuthoring.Build(source, AmpackKey.PrivateKeyPem);
    }

    [Fact]
    public async Task AMindAndTheCorpusItNeedsAreBoundRecalledUnloadedAndLoadedAgainOnNeo4j()
    {
        var members = new[] { "values", "tutor", "relationship" }.Select(MindMember).ToList();
        var composite = PackAuthoring.BuildComposite(PackAuthoring.FromModuleBundle(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["manifest.json"] = Encoding.UTF8.GetBytes($$"""
                {"format":"agentmemory.composite/v1alpha1","id":"{{MindFamilyId}}","version":"1.0.0",
                 "members":[{"id":"{{MindValues}}","version":"[1,2)"},{"id":"{{MindTutor}}","version":"[1,2)"},{"id":"io.example.minds.relationship","version":"[1,2)"}]}
                """),
        }), members, AmpackKey.PrivateKeyPem, AmpackTrust());
        var set = PackFamilies.LoadSet(PackAuthoring.BuildSet("io.example.minds.tutor-set", "1.0.0", [composite, .. members, WaterCycleCorpus()], AmpackKey.PrivateKeyPem, AmpackTrust()), AmpackTrust());
        var family = set.Families.Should().ContainSingle().Subject;
        var corpus = set.Packs.Should().ContainSingle().Subject;
        corpus.Id.Should().Be(MindCorpus);

        // Read, and bound to its knowledge before anything is switched on.
        var mind = MindReader.Read(family);
        mind.Profile.Should().Be("tutor/1.0");
        var binding = MindKnowledge.Bind(mind, set.Packs, DateTimeOffset.UtcNow);
        binding.IsComplete.Should().BeTrue(string.Join("; ", binding.Unmet));

        var importer = new KnowledgePackImporter(new Neo4jKnowledgePackStore(fixture.TransactionRunner));
        var knowledge = family.AllMembers.Where(m => m.Knowledge is not null).Append(corpus).ToList();
        knowledge.Select(k => k.Id).Should().BeEquivalentTo([MindValues, MindTutor, MindCorpus], "the relationship member is a definition: schema, no records");
        var question = new ContextRequest { SessionId = "s", Owner = "lea", Query = "Explain the water cycle: what happens during evaporation and condensation?" };
        try
        {
            foreach (var pack in knowledge) (await importer.ImportAsync(pack.Knowledge!)).Created.Should().BeGreaterThan(0, pack.Id);
            await using (var host = Host(m => m.UseNeo4jStore().AddFamily(family).AddPack(corpus)))
            {
                await using (var scope = host.CreateAsyncScope())
                {
                    var composed = scope.ServiceProvider.GetRequiredService<ModuleSet>();
                    (await new ModuleMigrator(fixture.TransactionRunner, composed.Resolution).ApplyAsync("mind and corpus")).Problems.Should().BeEmpty();
                }
                var recalled = await CompileAsync(host, question);
                OffersFrom(recalled, MindCorpus).Should().BeTrue("the corpus answers the learner's question");
                OffersFrom(recalled, MindTutor).Should().BeFalse("the Mind's own records are its instructions, never recalled as facts");
                OffersFrom(recalled, MindValues).Should().BeFalse("the Mind's values are instructions too");
            }

            // The knowledge unloaded: its records close, and the Mind's need is unmet, named.
            await importer.RemoveAsync(corpus.Manifest);
            (await LiveOfPackAsync(MindCorpus)).Should().Be(0);
            var unbound = MindKnowledge.Bind(mind, [], DateTimeOffset.UtcNow);
            unbound.IsComplete.Should().BeFalse("a Mind is not switched on in full while a need is unmet");
            unbound.Unmet.Should().ContainSingle().Which.Should().Contain("agentmemory.curriculum/1");

            // The Mind unloaded: its members' records close.
            foreach (var member in family.AllMembers.Where(m => m.Knowledge is not null)) await importer.RemoveAsync(member.Manifest);
            (await LiveOfPackAsync(MindValues)).Should().Be(0);
            (await LiveOfPackAsync(MindTutor)).Should().Be(0);

            // Loaded again: everything live, the need met, and the corpus recalled.
            foreach (var pack in knowledge) await importer.ImportAsync(pack.Knowledge!);
            (await LiveOfPackAsync(MindCorpus)).Should().Be(5);
            MindKnowledge.Bind(MindReader.Read(family), set.Packs, DateTimeOffset.UtcNow).IsComplete.Should().BeTrue();
            await using (var host = Host(m => m.UseNeo4jStore().AddFamily(family).AddPack(corpus)))
                OffersFrom(await CompileAsync(host, question), MindCorpus).Should().BeTrue();
        }
        finally
        {
            foreach (var id in new[] { MindValues, MindTutor, MindCorpus }) await importer.RemoveByIdAsync(id);
        }
    }
}
