#pragma warning disable AMEXT001
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Extensibility.Reading;
using AgentMemory.Packs.CodeMap;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

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

    [Fact]
    public async Task ACodeMapSnapshotWritesOnlyChangesClosesWhatIsGoneAndStillLeadsFromAFileToItsTests()
    {
        var store = new Neo4jModuleSnapshotStore(fixture.TransactionRunner);
        var at = DateTimeOffset.UtcNow;

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
    }
}
