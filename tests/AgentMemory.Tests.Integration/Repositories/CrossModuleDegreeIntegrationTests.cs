#pragma warning disable AMEXT001
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Extensibility.Reading;
using AgentMemory.Packs.CodeMap;
using AgentMemory.Packs.Work;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// Review 4, H1: an In neighbour's degree is the live keys in scope its links lead to, in both stores. A work item links to two
/// code-map files; a later code map no longer holds one. Neo4j keeps the item's link to the closed file, memory holds only what
/// is mounted: counting the item's own links gave 2 in Neo4j and 1 in memory, so the stores ranked the item differently.
/// </summary>
public sealed partial class ModuleRecordWriteIntegrationTests
{
    private static ModuleSnapshot CrossModuleMap(string source, bool withGone)
    {
        var builder = new CodeMapBuilder();
        builder.AddProject("src/Degree/Degree.csproj", "Acme.Degree");
        builder.AddFile("src/Degree/Keep.cs", "src/Degree/Degree.csproj");
        if (withGone) builder.AddFile("src/Degree/Gone.cs", "src/Degree/Degree.csproj");
        return builder.Build(source);
    }

    [Fact]
    public async Task AWorkItemsDegreeCountsOnlyTheFilesStillLiveInBothStores()
    {
        var store = new Neo4jModuleSnapshotStore(fixture.TransactionRunner);
        var at = DateTimeOffset.UtcNow.AddMinutes(40);
        await store.ImportAsync(NoCodeMap("git:clear"), at.AddSeconds(-2));
        await store.ImportAsync(NoWork("git:clear"), at.AddSeconds(-1));
        try
        {
            await store.ImportAsync(CrossModuleMap("git:degree-one", withGone: true), at);
            const string ledger = """{"tasks":[{"id":"DEGREE-1","task":"Touch both files","status":"Done","notes":"Review 4."}]}""";
            var work = WorkLedgerReader.Read(ledger, "tasks.json", [new WorkCommit("d1", "DEGREE-1: both", ["src/Degree/Keep.cs", "src/Degree/Gone.cs"])], "git:degree-one");
            (await store.ImportAsync(work, at.AddSeconds(1))).LinksLeftOut.Should().Be(0);
            var map = CrossModuleMap("git:degree-two", withGone: false);
            await store.ImportAsync(map, at.AddSeconds(2));

            using var services = Policies();
            var neo4j = new Neo4jModuleTraversalReader(fixture.TransactionRunner, work.Resolve(), services.GetRequiredService<IMemoryIsolationPolicy>());
            var context = new ModuleReadContext { Scope = MemoryScope.For("lea") };
            var memory = PackMemory.From([], [map, work]);
            async Task<int> Degree(IModuleTraversalReader reader)
            {
                var file = (await reader.MatchAsync(new() { ModuleId = CodeMapPack.Id, Type = CodeMapPack.FileType, Property = "path",
                    Match = ModuleAnchorMatch.Exact, Values = ["src/Degree/Keep.cs"], Limit = ModuleAnchorQuery.MaximumLimit }, context)).Records.Should().ContainSingle().Subject;
                var up = await reader.ExpandAsync(new() { ModuleId = WorkPack.Id, Relation = WorkPack.Changed, Direction = ModuleTraversalDirection.In,
                    FromIds = [file.Id] }, context);
                up.Status.Should().Be(ModuleReadStatus.Succeeded, string.Join("; ", up.Errors));
                return up.Edges.Should().ContainSingle().Subject.Degree;
            }

            (await Degree(memory)).Should().Be(1, "memory holds only the files mounted");
            (await Degree(neo4j)).Should().Be(1, "the item's link to the closed file does not count");
        }
        finally
        {
            await store.ImportAsync(NoWork("git:clear"), at.AddSeconds(3));
            await store.ImportAsync(NoCodeMap("git:clear"), at.AddSeconds(4));
        }
    }
}
