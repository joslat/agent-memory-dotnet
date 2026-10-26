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
/// The neighbour cap on a real Neo4j (red-team M3, PACK-review 1.4): under a type that 3,000 tests test, one expansion reads at
/// most 1,024 of them, and the profile of the store's own plan shows no step that touched all 3,000.
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
    public async Task AnExpansionFromAHubReadsAtMostTheScanCapNotEveryLink()
    {
        const int tests = 3000;
        var store = new Neo4jModuleSnapshotStore(fixture.TransactionRunner);
        var at = DateTimeOffset.UtcNow.AddMinutes(12);
        await store.ImportAsync(NoCodeMap("git:clear"), at.AddSeconds(-1));
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
        step.Edges.Should().HaveCount(6);

        // The store's own plan: no step pulled all 3,000 links of the hub, in either direction.
        var parameters = new Dictionary<string, object?>
        {
            ["owner"] = null, ["shared"] = true, ["fanOut"] = 6, ["words"] = Array.Empty<string>(), ["text"] = new[] { "name" },
            ["scan"] = ModuleExpandQuery.MaximumScan,
        };
        var into = await MostRowsAsync(ModuleTraversalQueries.In(CodeMapPack.TestType, "CODEMAP_TESTS", CodeMapPack.SymbolType),
            new(parameters) { ["ids"] = new[] { hub.Id } });
        into.Should().BeLessThanOrEqualTo(ModuleExpandQuery.MaximumScan + 1, "an In expansion reads at most the cap of the hub's tests");
        var test = step.Edges[0].To;
        var outOf = await MostRowsAsync(ModuleTraversalQueries.Out(CodeMapPack.TestType, "CODEMAP_TESTS", CodeMapPack.SymbolType),
            new(parameters) { ["ids"] = new[] { test.Id } });
        outOf.Should().BeLessThanOrEqualTo(ModuleExpandQuery.MaximumScan + 1, "the hub's degree is counted up to the cap, not over every link");

        await store.ImportAsync(NoCodeMap("git:clear"), at.AddSeconds(1));
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
