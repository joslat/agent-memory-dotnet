#pragma warning disable AMEXT001
using System.Text;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Bundles;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Extensibility.Reading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>Knowledge packs on a real Neo4j (Extensibility 0.13 §E2, E3, E6, E7): import, search, upgrade, removal.</summary>
public sealed partial class ModuleRecordWriteIntegrationTests
{
    private static readonly BundleKey PackKey = BundleSigning.CreateKey();

    private const string PackManifest = """
        {"format":"agentmemory.module/v1alpha1","id":"io.example.packdojo","version":"VERSION","sdk":"[0.1,0.2)",
         "seed":["seed/records.jsonl"],
         "ontology":{"labelPrefix":"PackDojo","types":[
           {"name":"PackDojoKata","scope":"shared","render":"{name}: {summary}","properties":{
             "name":{"type":"string","required":true,"maxLength":100},"summary":{"type":"string","maxLength":400}}},
           {"name":"PackDojoRank","scope":"shared","render":"{name}","properties":{"name":{"type":"string","required":true}}}],
           "relations":[{"name":"PACK_DOJO_KATA_FOR_RANK","from":"PackDojoKata","to":"PackDojoRank","max":1}]},
         "views":[{"id":"kata","type":"PackDojoKata","search":{"properties":["name","summary"]},"limit":12},
                  {"id":"history","type":"PackDojoKata","selection":"history","limit":10}]}
        """;

    private static string Rank(string key, string name) => $$$"""{"type":"PackDojoRank","key":"{{{key}}}","properties":{"name":"{{{name}}}"}}""";
    private static string Kata(string key, string name, string summary, string rank) =>
        $$$"""{"type":"PackDojoKata","key":"{{{key}}}","properties":{"name":"{{{name}}}","summary":"{{{summary}}}"},"links":[{"relation":"PACK_DOJO_KATA_FOR_RANK","target":"{{{rank}}}"}]}""";

    private static readonly string[] Version1 =
    [
        Kata("heian-shodan", "Heian Shodan", "First Heian kata: down block and lunge punch", "8-kyu"),
        Kata("heian-nidan", "Heian Nidan", "Second Heian kata: side kick from back stance", "7-kyu"),
        Rank("8-kyu", "8th kyu"), Rank("7-kyu", "7th kyu"),
    ];

    private static readonly string[] Version11 =
    [
        Kata("heian-nidan", "Heian Nidan", "Second Heian kata: side snap kick, knife hand block", "7-kyu"),
        Kata("heian-sandan", "Heian Sandan", "Third Heian kata: elbow strikes in horse stance", "6-kyu"),
        Rank("8-kyu", "8th kyu"), Rank("7-kyu", "7th kyu"), Rank("6-kyu", "6th kyu"),
    ];

    private static KnowledgePack DojoPack(string version, string[] seed)
    {
        var content = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [KnowledgePack.ManifestFile] = Encoding.UTF8.GetBytes(PackManifest.Replace("VERSION", version, StringComparison.Ordinal)),
            ["seed/records.jsonl"] = Encoding.UTF8.GetBytes(string.Join("\n", seed) + "\n"),
        };
        var trust = new BundleTrust();
        trust.DeveloperKeys.Add(PackKey.PublicKeyPem);
        return KnowledgePack.Open(BundleSigning.Sign(content, "io.example.packdojo", version, PackKey.PrivateKeyPem), trust);
    }

    [Fact]
    public async Task AKnowledgePackImportsIsSearchedByEveryOwnerUpgradesByKeyAndIsRemovedWithItsHistoryKept()
    {
        var v1 = DojoPack("1.0.0", Version1);
        var importer = new KnowledgePackImporter(new Neo4jKnowledgePackStore(fixture.TransactionRunner));
        (await importer.GetStateAsync(v1.Manifest)).Should().Be(new KnowledgePackState { PackId = v1.Id });

        // E2: the seed count, shared, with pack provenance; the same digest again writes nothing.
        var first = await importer.ImportAsync(v1);
        first.Should().Be(new KnowledgePackImportResult { PackId = v1.Id, Version = "1.0.0", Digest = v1.Digest, Created = 4 });
        (await importer.GetStateAsync(v1.Manifest)).Should().Be(new KnowledgePackState { PackId = v1.Id, Version = "1.0.0", LiveRecords = 4 });
        (await importer.ImportAsync(v1)).Should().Be(new KnowledgePackImportResult { PackId = v1.Id, Version = "1.0.0", Digest = v1.Digest, Unchanged = 4 });

        using var services = Policies();
        var reader = new Neo4jModuleReader(fixture.TransactionRunner, v1.Resolution, v1.Id, services.GetRequiredService<IMemoryIsolationPolicy>());
        var views = new ModuleViews(v1.Resolution, reader);
        var lea = new ModuleReadContext { Scope = MemoryScope.For("lea") };
        var found = await views.EvaluateWithArgumentsAsync("kata", new() { Search = "Which techniques are in Heian Shodan?" }, lea);
        found.Status.Should().Be(ModuleReadStatus.Succeeded);
        var best = found.Items.First();
        best.Record.Key.Should().Be("heian-shodan");
        best.Text.Should().Be("Heian Shodan: First Heian kata: down block and lunge punch");
        best.Record.OwnerId.Should().BeNull();
        best.Record.Source.MessageIds.Should().BeEmpty();
        best.Record.Source.Pack.Should().Be(new AgentMemory.Extensibility.Ingestion.PackSource
            { Id = v1.Id, Version = "1.0.0", Digest = v1.Digest, File = "seed/records.jsonl", Line = 1 });
        best.Record.Links.Single().Relation.Should().Be("PACK_DOJO_KATA_FOR_RANK");

        // E6: shared knowledge reaches every owner, and only scopes that include shared records.
        (await views.EvaluateWithArgumentsAsync("kata", new() { Search = "heian shodan" }, new() { Scope = MemoryScope.For("ned") }))
            .Items.Select(i => i.Record.Key).Should().Contain("heian-shodan");
        (await views.EvaluateWithArgumentsAsync("kata", new() { Search = "heian shodan" }, new() { Scope = MemoryScope.For("lea", includeShared: false) }))
            .Items.Should().BeEmpty();

        // E3: one record changed, one gone, two added; the changed record keeps both versions.
        var v11 = DojoPack("1.1.0", Version11);
        (await importer.ImportAsync(v11)).Should().Be(new KnowledgePackImportResult
            { PackId = v1.Id, Version = "1.1.0", Digest = v11.Digest, Created = 2, Changed = 1, Unchanged = 2, Closed = 1 });
        (await importer.GetStateAsync(v11.Manifest)).Should().Be(new KnowledgePackState { PackId = v1.Id, Version = "1.1.0", LiveRecords = 5 });
        (await views.EvaluateWithArgumentsAsync("kata", new() { Search = "heian" }, lea)).Items.Select(i => i.Record.Key)
            .Should().BeEquivalentTo(["heian-nidan", "heian-sandan"]);
        var history = await views.EvaluateWithArgumentsAsync("history", new() { Key = "heian-nidan" }, lea);
        history.Items.Select(i => i.Record.Source.Pack!.Version).Should().Equal("1.1.0", "1.0.0");
        (await FluentActions.Awaiting(() => importer.ImportAsync(v1)).Should().ThrowAsync<KnowledgePackException>())
            .Which.Problems.Single().Should().Contain("newer than 1.0.0");

        // E7: removal closes every live record; nothing is found, and the history stays.
        (await importer.RemoveAsync(v11.Manifest)).Should().Be(new KnowledgePackImportResult { PackId = v1.Id, Closed = 5 });
        (await importer.GetStateAsync(v11.Manifest)).Should().Be(new KnowledgePackState { PackId = v1.Id });
        (await views.EvaluateWithArgumentsAsync("kata", new() { Search = "heian" }, lea)).Items.Should().BeEmpty();
        (await views.EvaluateWithArgumentsAsync("history", new() { Key = "heian-nidan" }, lea)).Items.Should().HaveCount(2);
    }
}
