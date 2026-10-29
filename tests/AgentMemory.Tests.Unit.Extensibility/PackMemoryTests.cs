using System.Text;
using System.Text.Json.Nodes;
using AgentMemory.Abstractions.Options;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Context.Traversal;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Extensibility.Reading;
using AgentMemory.Packs.CodeMap;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// Packs in memory (PACK-engine) and their own tests (PACK-toolset, <c>evals/tests.json</c>): the real martial arts pack is
/// searched, listed in order and read by key with no database; a code map snapshot is walked from a file to its tests; a
/// scope without shared records and a pinned clock read nothing; and a pack's tests pass, or fail naming what is missing.
/// </summary>
public sealed class PackMemoryTests
{
    private static readonly string MartialArts = Path.Combine(AppContext.BaseDirectory, "Packs", "MartialArts");
    private static readonly ModuleReadContext Lea = new() { Scope = MemoryScope.For("lea") };
    private readonly PackSigningKey _key = PackAuthoring.CreateKey();

    private PackTrust Trust() => PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(_key.PublicKeyPem, "io.agentmemory", "io.example")));

    private static Dictionary<string, byte[]> Source() => new(PackAuthoring.FromModuleBundle(
        Directory.EnumerateFiles(MartialArts, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(MartialArts, f).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes, StringComparer.Ordinal)), StringComparer.Ordinal);

    private LoadedPack Load(Dictionary<string, byte[]> source) => PackLoader.Load(PackAuthoring.Build(source, _key.PrivateKeyPem), Trust());

    private static ModuleViews Views(PackMemory memory) => new(memory.Resolution, memory.ReaderFor("io.agentmemory.packs.martialarts"));

    [Fact]
    public async Task The_martial_arts_pack_is_searched_listed_in_order_and_read_by_key_with_no_database()
    {
        var memory = PackMemory.From([Load(Source()).Knowledge!]);

        memory.Count.Should().Be(93);
        var kata = await Views(memory).EvaluateWithArgumentsAsync("kata", new() { Search = "Which kata do I learn for 8th kyu in Shotokan? Heian Shodan" }, Lea);
        kata.Status.Should().Be(ModuleReadStatus.Succeeded);
        kata.Items.First().Record.Key.Should().Be("shotokan/heian-shodan");
        kata.Items.First().Record.Links.Should().Contain(l => l.Relation == "MARTIAL_ARTS_KATA_FOR");

        var ladder = await Views(memory).EvaluateWithArgumentsAsync("ladder", new(), Lea);
        var steps = ladder.Items.Select(i => i.Record.Properties["step"].GetInt32()).ToList();
        steps.Should().BeInAscendingOrder().And.HaveCount(17);

        (await Views(memory).EvaluateWithArgumentsAsync("ladder", new() { Key = "shotokan/8-kyu" }, Lea)).Items.Should().ContainSingle()
            .Which.Record.Properties["name"].GetString().Should().Be("8th kyu");
    }

    [Fact]
    public async Task A_scope_without_shared_records_or_a_pinned_clock_reads_nothing()
    {
        var memory = PackMemory.From([Load(Source()).Knowledge!]);

        (await Views(memory).EvaluateWithArgumentsAsync("kata", new() { Search = "heian" }, new() { Scope = MemoryScope.For("lea", includeShared: false) }))
            .Items.Should().BeEmpty();
        (await Views(memory).EvaluateWithArgumentsAsync("kata", new() { Search = "heian" }, Lea with { SystemAsOfUtc = DateTimeOffset.UtcNow.AddDays(-1) }))
            .Status.Should().NotBe(ModuleReadStatus.Succeeded);
    }

    [Fact]
    public async Task A_code_map_snapshot_in_memory_leads_from_a_file_to_the_tests_of_its_types()
    {
        const string store = "scip-dotnet nuget Acme.Map . Acme/Map/MapStore#";
        var builder = new CodeMapBuilder();
        builder.AddProject("src/Map/Map.csproj", "Acme.Map");
        builder.AddFile("src/Map/MapStore.cs", "src/Map/Map.csproj");
        builder.AddFile("tests/MapStoreTests.cs", null);
        builder.AddSymbol(store, "MapStore", "Acme.Map.MapStore", CodeMapSymbolKind.Class, "Acme.Map", "src/Map/MapStore.cs", 3, ["Close", "Remove"]);
        builder.AddTest("scip-dotnet nuget Acme.Map.Tests . Acme/Map/Tests/MapStoreTests#Remove_keeps_history().", "MapStoreTests.Remove_keeps_history",
            "tests/MapStoreTests.cs", 7, "xunit", [store]);
        var memory = PackMemory.From([], [builder.Build("git:abc1234")]);

        var walk = await TraversalWalk.RunAsync(memory.Resolution, memory,
            new ContextRequest { SessionId = "s", Owner = "lea", Query = "remove keeps history", Anchors = [ContextAnchor.Path("src/Map/MapStore.cs")] },
            CancellationToken.None);

        walk.Problems.Should().BeEmpty();
        walk.Items.Should().Contain(i => i.Record.Type == CodeMapPack.TestType && i.Record.Properties["name"].GetString() == "MapStoreTests.Remove_keeps_history");
        walk.Spent!.Reads.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Review1_M2_a_module_that_is_not_live_neither_seeds_steers_nor_offers_in_a_walk()
    {
        const string store = "scip-dotnet nuget Acme.Map . Acme/Map/MapStore#";
        var builder = new CodeMapBuilder();
        builder.AddProject("src/Map/Map.csproj", "Acme.Map");
        builder.AddFile("src/Map/MapStore.cs", "src/Map/Map.csproj");
        builder.AddSymbol(store, "MapStore", "Acme.Map.MapStore", CodeMapSymbolKind.Class, "Acme.Map", "src/Map/MapStore.cs", 3, ["Close"]);
        var memory = PackMemory.From([], [builder.Build("git:abc1234")]);
        var request = new ContextRequest { SessionId = "s", Owner = "lea", Query = "", Anchors = [ContextAnchor.Path("src/Map/MapStore.cs")] };

        (await TraversalWalk.RunAsync(memory.Resolution, memory, request, CancellationToken.None, live: _ => true)).Items.Should().NotBeEmpty();
        var expired = await TraversalWalk.RunAsync(memory.Resolution, memory, request, CancellationToken.None, live: _ => false);
        expired.Items.Should().BeEmpty("a pack past its validUntil takes no part in the walk");
        expired.Spent!.Reads.Should().Be(0);
    }

    [Fact]
    public void Review1_M9_a_knowledge_pack_carries_its_validUntil_to_the_module_it_composes_as()
    {
        var source = Source();
        var manifest = JsonNode.Parse(source["pack.json"])!.AsObject();
        manifest["validUntil"] = "2099-01-01T00:00:00Z";
        source["pack.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());

        var loaded = Load(source);

        loaded.ValidUntil.Should().Be(new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero));
        loaded.Knowledge!.ValidUntil.Should().Be(loaded.ValidUntil, "a host that composes the knowledge pack directly switches it off on time too");
    }

    [Fact]
    public async Task A_packs_own_tests_pass_and_a_failing_one_names_what_is_missing()
    {
        var source = Source();
        source[PackTests.FileName] = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["format"] = PackTests.Format,
            ["tests"] = new JsonArray(
                new JsonObject { ["name"] = "the first kata", ["view"] = "kata", ["search"] = "Which kata for 8th kyu? Heian Shodan", ["expect"] = new JsonArray("shotokan/heian-shodan"), ["top"] = 1 },
                new JsonObject { ["name"] = "a rank by key", ["view"] = "ladder", ["key"] = "shotokan/8-kyu", ["expect"] = new JsonArray("shotokan/8-kyu") },
                new JsonObject { ["name"] = "judo is not karate", ["view"] = "arts", ["search"] = "judo", ["expect"] = new JsonArray("judo"), ["notExpect"] = new JsonArray("shotokan") },
                new JsonObject { ["name"] = "wrong on purpose", ["view"] = "kata", ["search"] = "Heian Shodan", ["expect"] = new JsonArray("no-such-kata") }),
        }.ToJsonString());
        var pack = Load(source);
        pack.Tests.Should().NotBeNull("the tests travel in the signed pack");

        var results = await PackTests.RunAsync(pack, PackMemory.From([pack.Knowledge!]));

        results.Should().HaveCount(4);
        results.Take(3).Should().OnlyContain(r => r.Passed);
        results[3].Passed.Should().BeFalse();
        results[3].Detail.Should().Contain("missing: no-such-kata");
    }

    [Fact]
    public void A_malformed_tests_file_is_refused_with_the_test_it_concerns()
    {
        var source = Source();
        source[PackTests.FileName] = Encoding.UTF8.GetBytes("""{"format":"agentmemory.pack.tests/1.2","tests":[{"name":"both","view":"kata","anchors":[{"kind":"path","value":"x"}],"expect":["a"]}]}""");

        FluentActions.Awaiting(() => PackTests.RunAsync(Load(source), PackMemory.From([]))).Should().ThrowAsync<PackLoadException>()
            .Result.Which.Problems.Should().Contain(p => p.Contains("test 1") && p.Contains("exactly one"));
    }
}
