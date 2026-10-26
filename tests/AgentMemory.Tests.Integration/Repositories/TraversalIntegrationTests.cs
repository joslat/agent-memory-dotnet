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

/// <summary>
/// Traversal context on a real Neo4j (coding packs CMP-1): anchors match declared properties, hops follow declared links both
/// ways, a link written to an older version of its target still counts, scope holds, and anything undeclared is refused.
/// </summary>
public sealed partial class ModuleRecordWriteIntegrationTests
{
    private const string TraceManifest = """
        {"format":"agentmemory.module/v1alpha1","id":"io.example.tracecode","version":"VERSION","sdk":"[0.1,0.2)",
         "seed":["seed/records.jsonl"],
         "ontology":{"labelPrefix":"Trace","types":[
           {"name":"TraceFile","scope":"shared","render":"{path}","properties":{"path":{"type":"string","required":true}}},
           {"name":"TraceSymbol","scope":"shared","render":"{name}: {summary}","properties":{"name":{"type":"string","required":true},"summary":{"type":"string"}}},
           {"name":"TraceTest","scope":"shared","render":"Test {name}","properties":{"name":{"type":"string","required":true}}},
           {"name":"TraceDecision","scope":"shared","render":"{title}","properties":{"title":{"type":"string","required":true},"appliesTo":{"type":"string"}}}],
          "relations":[{"name":"TRACE_DEFINES","from":"TraceFile","to":"TraceSymbol"},{"name":"TRACE_TESTS","from":"TraceTest","to":"TraceSymbol"}]},
         "traversal":{
           "anchors":[{"type":"TraceFile","property":"path","match":"exact"},{"type":"TraceSymbol","property":"name","match":"exact","kind":"symbol"},
                      {"type":"TraceDecision","property":"appliesTo","match":"glob"}],
           "relations":[{"name":"TRACE_DEFINES","direction":"out","weight":0.9},{"name":"TRACE_TESTS","direction":"in","weight":0.8,"fanOut":8}],
           "hubs":["TraceFile","TraceSymbol"]}}
        """;

    private static string TraceRecord(string type, string key, string properties, string? links = null) =>
        "{\"type\":\"" + type + "\",\"key\":\"" + key + "\",\"properties\":" + properties + (links is null ? "" : ",\"links\":" + links) + "}";
    private static string Defines(string symbol) => "[{\"relation\":\"TRACE_DEFINES\",\"target\":\"" + symbol + "\"}]";
    private static string Tests(string symbol) => "[{\"relation\":\"TRACE_TESTS\",\"target\":\"" + symbol + "\"}]";

    private static string[] TraceSeed(string summary) =>
    [
        TraceRecord("TraceSymbol", "module-store", "{\"name\":\"ModuleStore\",\"summary\":\"" + summary + "\"}"),
        TraceRecord("TraceFile", "store-file", "{\"path\":\"src/Store/ModuleStore.cs\"}", Defines("module-store")),
        TraceRecord("TraceTest", "t-removal", "{\"name\":\"Removal keeps history\"}", Tests("module-store")),
        TraceRecord("TraceTest", "t-downgrade", "{\"name\":\"Downgrade is refused\"}", Tests("module-store")),
        TraceRecord("TraceTest", "t-upgrade", "{\"name\":\"Upgrade by key\"}", Tests("module-store")),
        TraceRecord("TraceDecision", "d-close", "{\"title\":\"Packs close records, never delete\",\"appliesTo\":\"src/Store/**\"}"),
        TraceRecord("TraceDecision", "d-docs", "{\"title\":\"Docs are projections\",\"appliesTo\":\"docs/**\"}"),
    ];

    private static KnowledgePack TracePack(string version, string[] seed)
    {
        var content = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [KnowledgePack.ManifestFile] = Encoding.UTF8.GetBytes(TraceManifest.Replace("VERSION", version, StringComparison.Ordinal)),
            ["seed/records.jsonl"] = Encoding.UTF8.GetBytes(string.Join("\n", seed) + "\n"),
        };
        var trust = new BundleTrust();
        trust.DeveloperKeys.Add(PackKey.PublicKeyPem);
        return KnowledgePack.Open(BundleSigning.Sign(content, "io.example.tracecode", version, PackKey.PrivateKeyPem), trust);
    }

    [Fact]
    public async Task TraversalMatchesDeclaredAnchorsFollowsLinksBothWaysAcrossVersionsAndRefusesTheUndeclared()
    {
        const string module = "io.example.tracecode";
        var v1 = TracePack("1.0.0", TraceSeed("Writes module records"));
        var importer = new KnowledgePackImporter(new Neo4jKnowledgePackStore(fixture.TransactionRunner));
        (await importer.ImportAsync(v1)).Created.Should().Be(7);
        using var services = Policies();
        var reader = new Neo4jModuleTraversalReader(fixture.TransactionRunner, v1.Resolution, services.GetRequiredService<IMemoryIsolationPolicy>());
        var lea = new ModuleReadContext { Scope = MemoryScope.For("lea") };

        // Anchors: an exact path matches without case; a glob declaration returns its candidates for the walk to match.
        var file = (await reader.MatchAsync(new() { ModuleId = module, Type = "TraceFile", Property = "path", Match = ModuleAnchorMatch.Exact,
            Values = [@"SRC\store\ModuleStore.cs"] }, lea)).Records.Should().ContainSingle().Subject;
        file.Key.Should().Be("store-file");
        (await reader.MatchAsync(new() { ModuleId = module, Type = "TraceDecision", Property = "appliesTo", Match = ModuleAnchorMatch.Glob,
            Values = ["src/Store/ModuleStore.cs"] }, lea)).Records.Select(r => r.Key).Should().BeEquivalentTo(["d-close", "d-docs"]);

        // Out: the file defines the symbol. In: the tests that test it, at most the fan-out asked for (never more than declared).
        var symbol = (await reader.ExpandAsync(new() { ModuleId = module, Relation = "TRACE_DEFINES", Direction = ModuleTraversalDirection.Out,
            FromIds = [file.Id] }, lea)).Edges.Should().ContainSingle().Subject.To;
        symbol.Key.Should().Be("module-store");
        var tests = await reader.ExpandAsync(new() { ModuleId = module, Relation = "TRACE_TESTS", Direction = ModuleTraversalDirection.In,
            FromIds = [symbol.Id], FanOut = 2 }, lea);
        tests.Edges.Should().HaveCount(2).And.OnlyContain(e => e.To.Type == "TraceTest" && e.FromId == symbol.Id);

        // The symbol changes in 1.1.0; the file and the tests do not, so they are not re-versioned and their links still point
        // at the symbol's first version. The out hop lands on the live symbol, and the in hop still finds every test.
        var v11 = TracePack("1.1.0", TraceSeed("Writes and closes module records"));
        var upgrade = await importer.ImportAsync(v11);
        upgrade.Changed.Should().Be(1);
        upgrade.Unchanged.Should().Be(6);
        var live = (await reader.ExpandAsync(new() { ModuleId = module, Relation = "TRACE_DEFINES", Direction = ModuleTraversalDirection.Out,
            FromIds = [file.Id] }, lea)).Edges.Should().ContainSingle().Subject.To;
        live.Id.Should().NotBe(symbol.Id);
        live.Properties["summary"].GetString().Should().Be("Writes and closes module records");
        (await reader.ExpandAsync(new() { ModuleId = module, Relation = "TRACE_TESTS", Direction = ModuleTraversalDirection.In,
            FromIds = [live.Id], FanOut = 8 }, lea)).Edges.Select(e => e.To.Key).Should().BeEquivalentTo(["t-removal", "t-downgrade", "t-upgrade"]);

        // Scope: shared pack records reach every owner, and only scopes that include shared records.
        (await reader.MatchAsync(new() { ModuleId = module, Type = "TraceFile", Property = "path", Match = ModuleAnchorMatch.Exact,
            Values = ["src/Store/ModuleStore.cs"] }, new() { Scope = MemoryScope.For("lea", includeShared: false) })).Records.Should().BeEmpty();

        // Only what the manifest declares: another property, another match, or the other direction is refused.
        (await reader.MatchAsync(new() { ModuleId = module, Type = "TraceSymbol", Property = "summary", Match = ModuleAnchorMatch.Exact,
            Values = ["x"] }, lea)).Status.Should().Be(ModuleReadStatus.Refused);
        (await reader.ExpandAsync(new() { ModuleId = module, Relation = "TRACE_TESTS", Direction = ModuleTraversalDirection.Out,
            FromIds = [file.Id] }, lea)).Status.Should().Be(ModuleReadStatus.Refused);
        (await reader.ExpandAsync(new() { ModuleId = module, Relation = "TRACE_DEFINES", Direction = ModuleTraversalDirection.Out,
            FromIds = [file.Id] }, lea with { SystemAsOfUtc = DateTimeOffset.UtcNow })).Status.Should().Be(ModuleReadStatus.NotApplicable);

        await importer.RemoveAsync(v11.Manifest);
    }
}
