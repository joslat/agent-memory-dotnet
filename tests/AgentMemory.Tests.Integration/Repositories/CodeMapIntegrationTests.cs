#pragma warning disable AMEXT001
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Extensibility.Reading;
using AgentMemory.Packs.CodeMap;
using AgentMemory.Packs.Work;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// The code map on a real Neo4j (coding packs CMP-2a): a snapshot per commit writes only what changed, closes what is gone,
/// leaves the records that link to a changed type alone, and the walk still leads from a file to the tests of its types.
/// </summary>
public sealed partial class ModuleRecordWriteIntegrationTests
{
    private const string MapStore = "scip-dotnet nuget Acme.Map . Acme/Map/MapStore#";
    private const string MapTest = "scip-dotnet nuget Acme.Map.Tests . Acme/Map/Tests/MapStoreTests#Remove_keeps_history().";
    private const string MapOther = "scip-dotnet nuget Acme.Map.Tests . Acme/Map/Tests/MapStoreTests#Other().";

    private static ModuleSnapshot MapSnapshot(string source, string members, bool withOther)
    {
        var builder = new CodeMapBuilder();
        builder.AddProject("src/Map/Map.csproj", "Acme.Map");
        builder.AddFile("src/Map/MapStore.cs", "src/Map/Map.csproj");
        builder.AddFile("tests/MapStoreTests.cs", null);
        builder.AddSymbol(MapStore, "MapStore", "Acme.Map.MapStore", CodeMapSymbolKind.Class, "Acme.Map", "src/Map/MapStore.cs", 3, members.Split(' '));
        builder.AddTest(MapTest, "MapStoreTests.Remove_keeps_history", "tests/MapStoreTests.cs", 7, "xunit", [MapStore]);
        if (withOther) builder.AddTest(MapOther, "MapStoreTests.Other", "tests/MapStoreTests.cs", 12, "xunit", [MapStore]);
        return builder.Build(source);
    }

    // The code map tests share one database: each starts and ends with no live code map record (closed, history kept).
    private static ModuleSnapshot NoCodeMap(string source) => new() { Module = CodeMapPack.Manifest, Source = source, Records = [] };
    private static ModuleSnapshot NoWork(string source) => new() { Module = WorkPack.Manifest, Dependencies = [CodeMapPack.Manifest], Source = source, Records = [] };

    [Fact]
    public async Task AWorkItemLinksToTheCodeMapFilesItsCommitsChangedAndAWalkClimbsFromTheFileToIt()
    {
        var store = new Neo4jModuleSnapshotStore(fixture.TransactionRunner);
        var at = DateTimeOffset.UtcNow.AddMinutes(4);
        await store.ImportAsync(NoCodeMap("git:clear"), at.AddSeconds(-2));
        await store.ImportAsync(NoWork("git:clear"), at.AddSeconds(-1));
        await store.ImportAsync(MapSnapshot("git:one", "Close Remove", withOther: false), at);
        const string ledger = """{"tasks":[{"id":"W-1","task":"Make the map store keep history","status":"Done","notes":"Asked for in review."}]}""";
        var work = WorkLedgerReader.Read(ledger, "tasks.json", [new WorkCommit("c1", "W-1: keep history", ["src/Map/MapStore.cs", "src/Map/Gone.cs"])], "git:one");

        // CODING-hierarchy: the link into the code map's records lands on the file it holds; the other is left out and counted.
        (await store.ImportAsync(work, at.AddSeconds(1)))
            .Should().Be(new ModuleSnapshotResult { ModuleId = WorkPack.Id, Source = "git:one", Created = 1, LinksLeftOut = 1 });
        (await store.ImportAsync(work with { Source = "git:two" }, at.AddSeconds(2)))
            .Should().Be(new ModuleSnapshotResult { ModuleId = WorkPack.Id, Source = "git:two", Unchanged = 1, LinksLeftOut = 1 });

        using var services = Policies();
        var reader = new Neo4jModuleTraversalReader(fixture.TransactionRunner, work.Resolve(), services.GetRequiredService<IMemoryIsolationPolicy>());
        var context = new ModuleReadContext { Scope = MemoryScope.For("lea") };
        var file = (await reader.MatchAsync(new() { ModuleId = CodeMapPack.Id, Type = CodeMapPack.FileType, Property = "path",
            Match = ModuleAnchorMatch.Exact, Values = ["src/Map/MapStore.cs"] }, context)).Records.Should().ContainSingle().Subject;
        var up = await reader.ExpandAsync(new() { ModuleId = WorkPack.Id, Relation = WorkPack.Changed, Direction = ModuleTraversalDirection.In,
            FromIds = [file.Id] }, context);
        up.Status.Should().Be(ModuleReadStatus.Succeeded, string.Join("; ", up.Errors));
        var item = up.Edges.Should().ContainSingle().Subject.To;
        item.ModuleId.Should().Be(WorkPack.Id);
        item.Properties["item"].GetString().Should().Be("W-1");
        item.Properties["commits"].GetInt64().Should().Be(1);

        await store.ImportAsync(NoWork("git:clear"), at.AddSeconds(3));
        await store.ImportAsync(NoCodeMap("git:clear"), at.AddSeconds(4));
    }

    [Fact]
    public async Task ACodeMapSnapshotWritesOnlyChangesClosesWhatIsGoneAndStillLeadsFromAFileToItsTests()
    {
        var store = new Neo4jModuleSnapshotStore(fixture.TransactionRunner);
        var at = DateTimeOffset.UtcNow.AddMinutes(1);
        await store.ImportAsync(NoCodeMap("git:clear"), at.AddSeconds(-1));

        (await store.ImportAsync(MapSnapshot("git:one", "Remove", withOther: true), at))
            .Should().Be(new ModuleSnapshotResult { ModuleId = CodeMapPack.Id, Source = "git:one", Created = 6 });
        // The same code at another commit writes nothing.
        (await store.ImportAsync(MapSnapshot("git:two", "Remove", withOther: true), at.AddSeconds(1)))
            .Should().Be(new ModuleSnapshotResult { ModuleId = CodeMapPack.Id, Source = "git:two", Unchanged = 6 });
        // One type changes: only it is written; the file that defines it and the tests that test it are left alone.
        (await store.ImportAsync(MapSnapshot("git:three", "Close Remove", withOther: true), at.AddSeconds(2)))
            .Should().Be(new ModuleSnapshotResult { ModuleId = CodeMapPack.Id, Source = "git:three", Changed = 1, Unchanged = 5 });
        // A test is deleted: it is closed, history kept.
        (await store.ImportAsync(MapSnapshot("git:four", "Close Remove", withOther: false), at.AddSeconds(3)))
            .Should().Be(new ModuleSnapshotResult { ModuleId = CodeMapPack.Id, Source = "git:four", Unchanged = 5, Closed = 1 });

        using var services = Policies();
        var reader = new Neo4jModuleTraversalReader(fixture.TransactionRunner, KnowledgePack.ResolutionOf(CodeMapPack.Manifest),
            services.GetRequiredService<IMemoryIsolationPolicy>());
        var context = new ModuleReadContext { Scope = MemoryScope.For("lea") };
        var file = (await reader.MatchAsync(new() { ModuleId = CodeMapPack.Id, Type = CodeMapPack.FileType, Property = "path",
            Match = ModuleAnchorMatch.Exact, Values = ["src/Map/MapStore.cs"] }, context)).Records.Should().ContainSingle().Subject;
        var symbol = (await reader.ExpandAsync(new() { ModuleId = CodeMapPack.Id, Relation = "CODEMAP_DEFINES",
            Direction = ModuleTraversalDirection.Out, FromIds = [file.Id] }, context)).Edges.Should().ContainSingle().Subject.To;
        symbol.Properties["members"].GetString().Should().Be("Close Remove");
        var tests = await reader.ExpandAsync(new() { ModuleId = CodeMapPack.Id, Relation = "CODEMAP_TESTS",
            Direction = ModuleTraversalDirection.In, FromIds = [symbol.Id] }, context);
        tests.Edges.Select(e => e.To.Properties["name"].GetString()).Should().Equal("MapStoreTests.Remove_keeps_history");
        tests.Edges.Single().To.Source.Pack!.Version.Should().Be("git:one", "the test was written at the first commit and never changed");
        await store.ImportAsync(NoCodeMap("git:clear"), at.AddSeconds(4));
    }

    private const string UseA = "scip-dotnet nuget Acme.Use . Acme/Use/A#";
    private const string UseB = "scip-dotnet nuget Acme.Use . Acme/Use/B#";
    private const string UseC = "scip-dotnet nuget Acme.Use . Acme/Use/C#";
    private const string UseTest = "scip-dotnet nuget Acme.Use.Tests . Acme/Use/Tests/UseTests#Works().";

    // A and B use each other; the test tests A, and B unless dropped; with C, A also uses C and C uses A.
    private static ModuleSnapshot UseSnapshot(string source, bool testsB, bool withC)
    {
        var builder = new CodeMapBuilder();
        builder.AddProject("src/Use/Use.csproj", "Acme.Use");
        builder.AddFile("src/Use/AB.cs", "src/Use/Use.csproj");
        builder.AddFile("tests/UseTests.cs", null);
        builder.AddSymbol(UseA, "A", "Acme.Use.A", CodeMapSymbolKind.Class, "Acme.Use", "src/Use/AB.cs", 1, ["Run"]);
        builder.AddSymbol(UseB, "B", "Acme.Use.B", CodeMapSymbolKind.Class, "Acme.Use", "src/Use/AB.cs", 9, ["Help"]);
        builder.AddUses(UseA, withC ? [UseB, UseC] : [UseB]);
        builder.AddUses(UseB, [UseA]);
        if (withC)
        {
            builder.AddFile("src/Use/C.cs", "src/Use/Use.csproj");
            builder.AddSymbol(UseC, "C", "Acme.Use.C", CodeMapSymbolKind.Class, "Acme.Use", "src/Use/C.cs", 1, ["Call"]);
            builder.AddUses(UseC, [UseA]);
        }
        builder.AddTest(UseTest, "UseTests.Works", "tests/UseTests.cs", 5, "xunit", testsB ? [UseA, UseB] : [UseA]);
        return builder.Build(source);
    }

    [Fact]
    public async Task ASnapshotWritesTypesThatUseEachOtherDropsALinkExactlyAndThenWritesNothing()
    {
        var store = new Neo4jModuleSnapshotStore(fixture.TransactionRunner);
        var at = DateTimeOffset.UtcNow.AddMinutes(2);
        await store.ImportAsync(NoCodeMap("git:clear"), at.AddSeconds(-1));
        using var services = Policies();
        var reader = new Neo4jModuleTraversalReader(fixture.TransactionRunner, KnowledgePack.ResolutionOf(CodeMapPack.Manifest),
            services.GetRequiredService<IMemoryIsolationPolicy>());
        var context = new ModuleReadContext { Scope = MemoryScope.For("lea") };
        async Task<string> Live(string scip) => (await reader.MatchAsync(new() { ModuleId = CodeMapPack.Id, Type = CodeMapPack.SymbolType,
            Property = "scip", Match = ModuleAnchorMatch.Exact, Values = [scip] }, context)).Records.Should().ContainSingle().Subject.Id;
        async Task<string[]> In(string relation, string scip) => [.. (await reader.ExpandAsync(new() { ModuleId = CodeMapPack.Id, Relation = relation,
            Direction = ModuleTraversalDirection.In, FromIds = [await Live(scip)], FanOut = 8 }, context)).Edges
            .Select(e => e.To.Properties["name"].GetString()!).Order(StringComparer.Ordinal)];
        // Uses links are stored but not walked (CMP-2c), so they are read from the store itself, as the walk reads links: from
        // live sources to any version of the target (every version shares its key identity).
        Task<string[]> UsedBy(string scip) => fixture.TransactionRunner.ReadAsync(async runner => (await (await runner.RunAsync(
            "MATCH (t {scip:$scip}) WITH DISTINCT t.key_id AS key MATCH (s)-[:CODEMAP_USES]->({key_id:key}) "
            + "WHERE s.invalidated_at IS NULL AND s.superseded_by IS NULL RETURN DISTINCT s.name AS name ORDER BY name", new { scip }))
            .ToListAsync(r => ValueExtensions.As<string>(r["name"]))).ToArray());

        // A and B use each other: whichever is written first links to the other once it exists. The same snapshot again
        // writes nothing: what is stored is what the snapshot holds.
        (await store.ImportAsync(UseSnapshot("git:one", testsB: true, withC: false), at))
            .Should().Be(new ModuleSnapshotResult { ModuleId = CodeMapPack.Id, Source = "git:one", Created = 6 });
        (await UsedBy(UseA)).Should().Equal("B");
        (await UsedBy(UseB)).Should().Equal("A");
        (await store.ImportAsync(UseSnapshot("git:two", testsB: true, withC: false), at.AddSeconds(1)))
            .Should().Be(new ModuleSnapshotResult { ModuleId = CodeMapPack.Id, Source = "git:two", Unchanged = 6 });

        // The test stops using B: its link to B goes (a snapshot holds every link), and the next import writes nothing again.
        (await store.ImportAsync(UseSnapshot("git:three", testsB: false, withC: false), at.AddSeconds(2)))
            .Should().Be(new ModuleSnapshotResult { ModuleId = CodeMapPack.Id, Source = "git:three", Changed = 1, Unchanged = 5 });
        (await In("CODEMAP_TESTS", UseB)).Should().BeEmpty();
        (await In("CODEMAP_TESTS", UseA)).Should().Equal("UseTests.Works");
        (await store.ImportAsync(UseSnapshot("git:four", testsB: false, withC: false), at.AddSeconds(3)))
            .Should().Be(new ModuleSnapshotResult { ModuleId = CodeMapPack.Id, Source = "git:four", Unchanged = 6 });

        // A, otherwise unchanged, now also uses the new C, which uses A: A gains a link, so it is a new version.
        (await store.ImportAsync(UseSnapshot("git:five", testsB: false, withC: true), at.AddSeconds(4)))
            .Should().Be(new ModuleSnapshotResult { ModuleId = CodeMapPack.Id, Source = "git:five", Created = 2, Changed = 1, Unchanged = 5 });
        (await UsedBy(UseA)).Should().Equal("B", "C");
        (await UsedBy(UseC)).Should().Equal("A");
        (await store.ImportAsync(UseSnapshot("git:six", testsB: false, withC: true), at.AddSeconds(5)))
            .Should().Be(new ModuleSnapshotResult { ModuleId = CodeMapPack.Id, Source = "git:six", Unchanged = 8 });
        await store.ImportAsync(NoCodeMap("git:clear"), at.AddSeconds(6));
    }
}
