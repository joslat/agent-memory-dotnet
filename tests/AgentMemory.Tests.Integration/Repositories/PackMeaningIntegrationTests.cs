#pragma warning disable AMEXT001
using System.Text;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Bundles;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Extensibility.Reading;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// Packs searched by meaning (feature 19) on a real Neo4j: a reworded question is found by meaning and not by words; the record
/// nodes are as a words-only import leaves them; another model's vectors are never compared; a judge decides what is offered, and
/// one that fails offers nothing; closed records keep no vector.
/// </summary>
public sealed partial class ModuleRecordWriteIntegrationTests
{
    private const string MeaningManifest = """
        {"format":"agentmemory.module/v1alpha1","id":"io.example.packmeaning","version":"VERSION","sdk":"[0.1,0.2)",
         "seed":["seed/records.jsonl"],
         "ontology":{"labelPrefix":"PackMeaning","types":[
           {"name":"PackMeaningNote","scope":"shared","render":"{title}: {text}","properties":{
             "title":{"type":"string","required":true,"maxLength":100},"text":{"type":"string","maxLength":400}}}]},
         "views":[{"id":"notes","type":"PackMeaningNote","search":{"properties":["title","text"]},"limit":3}]}
        """;

    private static KnowledgePack MeaningPack(string version = "1.0.0", string pets = "Dogs stay in the office, never on the floor.")
    {
        static string Note(string key, string title, string text) =>
            $$$"""{"type":"PackMeaningNote","key":"{{{key}}}","properties":{"title":"{{{title}}}","text":"{{{text}}}"}}""";
        var content = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [KnowledgePack.ManifestFile] = Encoding.UTF8.GetBytes(MeaningManifest.Replace("VERSION", version, StringComparison.Ordinal)),
            ["seed/records.jsonl"] = Encoding.UTF8.GetBytes(string.Join("\n",
                Note("pets", "Pets", pets),
                Note("kiln", "Kiln", "Only the steward fires the kiln."),
                Note("hours", "Hours", "Open Tuesday to Saturday.")) + "\n"),
        };
        var trust = new BundleTrust();
        trust.DeveloperKeys.Add(PackKey.PublicKeyPem);
        return KnowledgePack.Open(BundleSigning.Sign(content, "io.example.packmeaning", version, PackKey.PrivateKeyPem), trust);
    }

    // Vectors by concept, so meaning is deterministic: a text points along each concept it names a word of.
    private sealed class ConceptEmbedder(string model) : IEmbeddingGenerator<string, Embedding<float>>
    {
        // Three concepts, and a fourth for a text that names none: each note names exactly one.
        private static readonly string[][] Concepts =
            [["dog", "dogs", "puppy", "pet", "pets", "animal"], ["kiln", "oven", "pottery", "fires"], ["open", "monday", "tuesday", "saturday", "hours"]];

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(text =>
            {
                var words = text.ToLowerInvariant().Split([' ', '.', ',', '?', ':'], StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                var vector = Concepts.Select(c => c.Any(words.Contains) ? 1f : 0f).Append(0f).ToArray();
                if (vector.All(x => x == 0)) vector[^1] = 1f;
                return new Embedding<float>(vector);
            })));

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(EmbeddingGeneratorMetadata) ? new EmbeddingGeneratorMetadata("concepts", null, model, 4) : null;

        public void Dispose() { }
    }

    private sealed class Judge(Func<string, string, bool> answers) : IPackMeaningJudge
    {
        public int Asked { get; private set; }
        public Task<bool> AnswersAsync(string question, string text, CancellationToken cancellationToken = default)
        {
            Asked++;
            return Task.FromResult(answers(question, text));
        }
    }

    private async Task<IReadOnlyList<string>> FoundAsync(KnowledgePack pack, PackMeaning? meaning, string question)
    {
        using var services = Policies();
        var reader = new Neo4jModuleReader(fixture.TransactionRunner, pack.Resolution, pack.Id, services.GetRequiredService<IMemoryIsolationPolicy>(), meaning: meaning);
        var found = await new ModuleViews(pack.Resolution, reader).EvaluateWithArgumentsAsync("notes", new() { Search = question },
            new ModuleReadContext { Scope = MemoryScope.For("lea") });
        found.Status.Should().Be(ModuleReadStatus.Succeeded);
        return [.. found.Items.Select(i => i.Record.Key)];
    }

    private async Task<List<IRecord>> RowsAsync(string query) =>
        await fixture.TransactionRunner.ReadAsync(async t => await (await t.RunAsync(query)).ToListAsync());

    [Fact]
    public async Task APackSearchedByMeaningFindsAReworded_question_LeavesItsRecordsAsTheyWere_AndClosedRecordsKeepNoVector()
    {
        var pack = MeaningPack();
        var meaning = new PackMeaning(new ConceptEmbedder("concepts-1"), new() { MinimumScore = 0.8, KeepWords = false });
        var importer = new KnowledgePackImporter(new Neo4jKnowledgePackStore(fixture.TransactionRunner, null, meaning));
        (await importer.ImportAsync(pack)).Created.Should().Be(3);

        // The record nodes carry nothing new: the vectors are nodes of their own, one per record, tagged with the model.
        var records = await RowsAsync("MATCH (n:PackMeaningNote) RETURN labels(n) AS labels, keys(n) AS keys");
        records.Should().HaveCount(3).And.OnlyContain(r => !ValueExtensions.As<List<string>>(r["keys"]).Any(k => k.Contains("vector") || k.Contains("meaning")));
        records.Select(r => ValueExtensions.As<List<string>>(r["labels"])).Should().OnlyContain(l => !l.Any(x => x.StartsWith("AmpackMeaning")));
        (await RowsAsync("MATCH (v:AmpackMeaning4)-[:AMPACK_MEANS]->(:PackMeaningNote) RETURN v.model AS model"))
            .Select(r => ValueExtensions.As<string>(r["model"])).Should().Equal("concepts-1", "concepts-1", "concepts-1");
        (await importer.ImportAsync(pack)).Unchanged.Should().Be(3);
        (await RowsAsync("MATCH (v:AmpackMeaning4)-[:AMPACK_MEANS]->(:PackMeaningNote) RETURN v")).Should().HaveCount(3, "a second import embeds nothing again");

        // A question that shares no word with the record: words find nothing, meaning finds it.
        const string question = "Can I bring my puppy?";
        (await FoundAsync(pack, null, question)).Should().BeEmpty();
        (await FoundAsync(pack, meaning, question)).Should().Equal("pets");
        // Another model's vectors are never compared: by words alone again.
        (await FoundAsync(pack, new PackMeaning(new ConceptEmbedder("concepts-2"), new() { KeepWords = false }), question)).Should().BeEmpty();

        // A judge decides what is offered; one that says no, or fails, offers nothing.
        var low = new PackMeaningOptions { MinimumScore = 0.5, KeepWords = false, Judged = 3 };
        var yes = new Judge((_, text) => text.Contains("Dogs", StringComparison.Ordinal));
        (await FoundAsync(pack, new PackMeaning(new ConceptEmbedder("concepts-1"), low, yes), question)).Should().Equal("pets");
        yes.Asked.Should().BeInRange(1, 3);
        (await FoundAsync(pack, new PackMeaning(new ConceptEmbedder("concepts-1"), low, new Judge((_, _) => false)), question)).Should().BeEmpty();
        (await FoundAsync(pack, new PackMeaning(new ConceptEmbedder("concepts-1"), low, new Judge((_, _) => throw new HttpRequestException("down"))), question))
            .Should().BeEmpty("a judge that fails offers nothing");

        // An upgrade that changes a record: the new version gets a vector, the closed one keeps none (feature 19: closed versions'
        // vectors crowded out the live records).
        var upgrade = MeaningPack("1.1.0", "Dogs and other pets stay in the office.");
        (await importer.ImportAsync(upgrade)).Changed.Should().Be(1);
        (await RowsAsync("MATCH (v:AmpackMeaning4)-[:AMPACK_MEANS]->(n:PackMeaningNote) RETURN n.invalidated_at IS NULL AS live"))
            .Select(r => ValueExtensions.As<bool>(r["live"])).Should().Equal(true, true, true);
        (await FoundAsync(upgrade, meaning, question)).Should().Equal("pets");

        // Removed, the records keep no vector; the history stays.
        await importer.RemoveAsync(upgrade.Manifest);
        (await RowsAsync("MATCH (v:AmpackMeaning4) RETURN v")).Should().BeEmpty();
        (await RowsAsync("MATCH (n:PackMeaningNote) RETURN n")).Should().HaveCount(4, "three records and the changed one's first version");
        (await FoundAsync(upgrade, meaning, question)).Should().BeEmpty();
    }
}
