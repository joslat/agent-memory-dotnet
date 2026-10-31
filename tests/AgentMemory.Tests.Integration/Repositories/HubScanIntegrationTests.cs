#pragma warning disable AMEXT001
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Queries;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Extensibility.Reading;
using AgentMemory.Packs.CodeMap;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// Hubs on a real Neo4j (red-team M3, review 2): under a type that 3,000 tests test, one expansion ranks every one of them, so
/// it chooses what any store would (the first by key among equal ranks); a record past the link limit is not expanded, and the
/// check that finds it reads the store's degree, not the links.
/// </summary>
public sealed partial class ModuleRecordWriteIntegrationTests
{
    private const string Hub = "scip-dotnet nuget Acme.Hub . Acme/Hub/Hub#";

    private static ModuleSnapshot HubSnapshot(int tests)
    {
        var builder = new CodeMapBuilder();
        builder.AddProject("src/Hub/Hub.csproj", "Acme.Hub");
        builder.AddFile("src/Hub/Hub.cs", "src/Hub/Hub.csproj");
        builder.AddFile("tests/HubTests.cs", null);
        builder.AddSymbol(Hub, "Hub", "Acme.Hub.Hub", CodeMapSymbolKind.Class, "Acme.Hub", "src/Hub/Hub.cs", 1, ["Run"]);
        for (var i = 0; i < tests; i++)
            builder.AddTest($"scip-dotnet nuget Acme.Hub.Tests . Acme/Hub/Tests/HubTests#Case{i:0000}().", $"HubTests.Case{i:0000}",
                "tests/HubTests.cs", 10 + i, "xunit", [Hub]);
        return builder.Build("git:hub");
    }

    [Fact]
    public async Task AnExpansionRanksEveryLinkOfAHubWithinTheLimitAndTheHubCheckReadsTheDegreeNotTheLinks()
    {
        const int tests = 3000;
        var store = new Neo4jModuleSnapshotStore(fixture.TransactionRunner);
        var at = DateTimeOffset.UtcNow.AddMinutes(12);
        await store.ImportAsync(NoCodeMap("git:clear"), at.AddSeconds(-1));
        try
        {
            (await store.ImportAsync(HubSnapshot(tests), at)).Created.Should().Be(tests + 4);

            using var services = Policies();
            var reader = new Neo4jModuleTraversalReader(fixture.TransactionRunner, KnowledgePack.ResolutionOf(CodeMapPack.Manifest),
                services.GetRequiredService<IMemoryIsolationPolicy>());
            var context = new ModuleReadContext { Scope = MemoryScope.For("lea") };
            var file = (await reader.MatchAsync(new() { ModuleId = CodeMapPack.Id, Type = CodeMapPack.FileType, Property = "path",
                Match = ModuleAnchorMatch.Exact, Values = ["src/Hub/Hub.cs"] }, context)).Records.Should().ContainSingle().Subject;
            var hub = (await reader.ExpandAsync(new() { ModuleId = CodeMapPack.Id, Relation = "CODEMAP_DEFINES",
                Direction = ModuleTraversalDirection.Out, FromIds = [file.Id] }, context)).Edges.Should().ContainSingle().Subject.To;
            var step = await reader.ExpandAsync(new() { ModuleId = CodeMapPack.Id, Relation = "CODEMAP_TESTS",
                Direction = ModuleTraversalDirection.In, FromIds = [hub.Id], FanOut = 6 }, context);
            step.Status.Should().Be(ModuleReadStatus.Succeeded, string.Join("; ", step.Errors));
            step.Errors.Should().BeEmpty("3,000 links are within the limit");

            // Every test ranks alike (no question words, one link each), so the six are the first six by key, as in any store.
            var firstSix = await ScalarsAsync($"MATCH (t:`{CodeMapPack.TestType}`) WHERE t.invalidated_at IS NULL RETURN t.key AS key ORDER BY key LIMIT 6");
            step.Edges.Select(e => e.To.Key).Should().Equal(firstSix);

            // Past the limit a record is not expanded; the live expansion finds that from the hub's degree, not its 3,000 links
            // (review 7, W12: this checked a copy of the hub statement no store ran).
            var text = KnowledgePack.ResolutionOf(CodeMapPack.Manifest).Modules.Single().Ontology!.Types.Single(t => t.Name == CodeMapPack.TestType).TextProperties().ToArray();
            var parameters = new Dictionary<string, object?>
            {
                ["ids"] = new[] { hub.Id }, ["maxLinks"] = 100, ["over"] = 101, ["owner"] = null, ["shared"] = true, ["fanOut"] = 6,
                ["validAt"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["words"] = Array.Empty<string>(), ["text"] = text, ["scan"] = ModuleExpandQuery.MaximumScan, ["wide"] = ModuleTraversalQueries.WideKey,
                ["fold"] = Enumerable.Range('A', 26).Select(c => new[] { ((char)c).ToString(), ((char)(c + 32)).ToString() }).ToArray(),
            };
            var expand = ModuleTraversalQueries.In(CodeMapPack.TestType, "CODEMAP_TESTS", CodeMapPack.SymbolType);
            (await LinksAsync(expand, parameters)).Should().Equal([101L], "3,000 links are past a limit of 100, counted no further than one past it");
            (await MostRowsAsync(expand, parameters)).Should().BeLessThan(tests, "the check counts the degree without reading every link");
        }
        finally { await store.ImportAsync(NoCodeMap("git:clear"), at.AddSeconds(1)); }
    }

    private async Task<IReadOnlyList<string>> ScalarsAsync(string query)
    {
        await using var session = fixture.Driver.AsyncSession();
        var cursor = await session.RunAsync(query);
        return [.. (await cursor.ToListAsync()).Select(r => global::Neo4j.Driver.ValueExtensions.As<string>(r["key"]))];
    }

    // The link counts of the rows past the limit.
    private async Task<IReadOnlyList<long>> LinksAsync(string query, Dictionary<string, object?> parameters)
    {
        await using var session = fixture.Driver.AsyncSession();
        var cursor = await session.RunAsync(query, parameters);
        return [.. (await cursor.ToListAsync()).Select(r => global::Neo4j.Driver.ValueExtensions.As<long>(r["links"])).Where(links => links > 100)];
    }

    // The most rows any step of the store's plan produced, from PROFILE.
    private async Task<long> MostRowsAsync(string query, Dictionary<string, object?> parameters)
    {
        await using var session = fixture.Driver.AsyncSession();
        var cursor = await session.RunAsync("PROFILE " + query, parameters);
        var summary = await cursor.ConsumeAsync();
        static long Most(IProfiledPlan plan) => Math.Max(plan.Records, plan.Children.Select(Most).DefaultIfEmpty(0).Max());
        return Most(summary.Profile);
    }
}
