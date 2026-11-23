#pragma warning disable AMEXT001
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Context.Traversal;
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
/// Link states on a real Neo4j (CO-2c): a link keeps pointing at the target version it was made against, so a target that changed
/// after the link was made is known from the versions the store already keeps. The reader says so on the edge and the walk on the
/// item it reached, as data: its reasons, scores and order do not change.
/// </summary>
public sealed partial class ModuleRecordWriteIntegrationTests
{
    private const string StateType = "scip-dotnet nuget Acme.LinkState . Acme/LinkState/StateStore#";
    private const string StateTest = "scip-dotnet nuget Acme.LinkState.Tests . Acme/LinkState/Tests/StateStoreTests#Remove_keeps_history().";

    private static ModuleSnapshot StateMap(string source, string members, string framework = "xunit")
    {
        var builder = new CodeMapBuilder();
        builder.AddProject("src/LinkState/LinkState.csproj", "Acme.LinkState");
        builder.AddFile("src/LinkState/StateStore.cs", "src/LinkState/LinkState.csproj");
        builder.AddFile("tests/StateStoreTests.cs", null);
        builder.AddSymbol(StateType, "StateStore", "Acme.LinkState.StateStore", CodeMapSymbolKind.Class, "Acme.LinkState", "src/LinkState/StateStore.cs", 3, members.Split(' '));
        builder.AddTest(StateTest, "StateStoreTests.Remove_keeps_history", "tests/StateStoreTests.cs", 7, framework, [StateType]);
        return builder.Build(source);
    }

    [Fact]
    public async Task ALinkWhoseTargetChangedSinceItWasMadeIsSuspectOnTheEdgeAndTheWalkItemWithNoScoreOrReasonMoved()
    {
        var store = new Neo4jModuleSnapshotStore(fixture.TransactionRunner);
        var at = DateTimeOffset.UtcNow.AddMinutes(70);
        await store.ImportAsync(NoCodeMap("git:clear"), at.AddSeconds(-1));
        try
        {
            using var services = Policies();
            var resolution = CodingResolutions.CodeMap;
            var reader = new Neo4jModuleTraversalReader(fixture.TransactionRunner, resolution, services.GetRequiredService<IMemoryIsolationPolicy>());
            var request = new ContextRequest { SessionId = "s", Owner = "lea", Query = "", Anchors = [new ContextAnchor(ContextAnchorKind.Path, "src/LinkState/StateStore.cs")] };
            static IEnumerable<(string, string, long, long, int, int, int, string)> Offers(TraversalResult walk) =>
                walk.Items.Select(i => (i.Record.Type, i.Record.Key, i.Score, i.Rank, i.Hops, i.SharedBy, i.Matches, i.Via));
            const string symbolVia = "StateStore.cs → defines StateStore";
            const string testVia = "StateStore.cs → defines StateStore → StateStoreTests.Remove_keeps_history tests it";

            // Every link points at a live version: nothing is suspect.
            await store.ImportAsync(StateMap("git:state-one", "Remove"), at);
            var fresh = await TraversalWalk.RunAsync(resolution, reader, request, CancellationToken.None);
            fresh.Problems.Should().BeEmpty();
            fresh.Items.Single(i => i.Record.Type == CodeMapPack.TestType).Via.Should().Be(testVia);
            fresh.Items.Should().OnlyContain(i => !i.ChangedSinceLinked);

            // The type changes; the file that defines it and the test that tests it do not, so they are not written again and their
            // links still point at the type's first version. Both hops through those links are suspect: the walk carries it on the
            // items those hops reached, and its reasons stay exactly as they were (R-WALK-066).
            (await store.ImportAsync(StateMap("git:state-two", "Close Remove"), at.AddSeconds(1)))
                .Should().Be(new ModuleSnapshotResult { ModuleId = CodeMapPack.Id, Source = "git:state-two", Changed = 1, Unchanged = 4 });
            var changed = await TraversalWalk.RunAsync(resolution, reader, request, CancellationToken.None);
            changed.Problems.Should().BeEmpty();
            var changedSymbol = changed.Items.Single(i => i.Record.Type == CodeMapPack.SymbolType);
            changedSymbol.ChangedSinceLinked.Should().BeTrue();
            changedSymbol.Via.Should().Be(symbolVia);
            var changedTest = changed.Items.Single(i => i.Record.Type == CodeMapPack.TestType);
            changedTest.ChangedSinceLinked.Should().BeTrue();
            changedTest.Via.Should().Be(testVia);
            changed.Items.Single(i => i.Record.Type == CodeMapPack.FileType).ChangedSinceLinked.Should().BeFalse("an anchor's own record was reached by no link");
            // Data only: the same records, scores, ranks, hops, specificity, matches, reasons and order (CMP-4's trial results do
            // not move).
            Offers(changed).Should().Equal(Offers(fresh));

            // The reader says it on each edge, in both directions, from the same statement.
            var context = new ModuleReadContext { Scope = MemoryScope.For("lea") };
            var file = changed.Items.Single(i => i.Record.Type == CodeMapPack.FileType).Record;
            var defines = await reader.ExpandAsync(new() { ModuleId = CodeMapPack.Id, Relation = "CODEMAP_DEFINES", Direction = ModuleTraversalDirection.Out,
                FromIds = [file.Id] }, context);
            var symbol = defines.Edges.Should().ContainSingle().Subject;
            symbol.State.Should().Be(ModuleLinkState.Suspect, "the file links to the type's first version");
            defines.DanglingLinks.Should().Be(0);
            async Task<ModuleTraversalEdge> TestedBy() => (await reader.ExpandAsync(new() { ModuleId = CodeMapPack.Id, Relation = "CODEMAP_TESTS",
                Direction = ModuleTraversalDirection.In, FromIds = [symbol.To.Id] }, context)).Edges.Should().ContainSingle().Subject;
            (await TestedBy()).State.Should().Be(ModuleLinkState.Suspect, "the test links to the type's first version");

            // The test changes: it is written again, with its link to the type's live version, so its link is current again; the
            // file's is still suspect.
            (await store.ImportAsync(StateMap("git:state-three", "Close Remove", framework: "nunit"), at.AddSeconds(2)))
                .Should().Be(new ModuleSnapshotResult { ModuleId = CodeMapPack.Id, Source = "git:state-three", Changed = 1, Unchanged = 4 });
            (await TestedBy()).State.Should().Be(ModuleLinkState.Current);
            (await reader.ExpandAsync(new() { ModuleId = CodeMapPack.Id, Relation = "CODEMAP_DEFINES", Direction = ModuleTraversalDirection.Out,
                FromIds = [file.Id] }, context)).Edges.Should().ContainSingle().Which.State.Should().Be(ModuleLinkState.Suspect);
            var later = await TraversalWalk.RunAsync(resolution, reader, request, CancellationToken.None);
            later.Items.Single(i => i.Record.Type == CodeMapPack.SymbolType).ChangedSinceLinked.Should().BeTrue();
            var laterTest = later.Items.Single(i => i.Record.Type == CodeMapPack.TestType);
            laterTest.ChangedSinceLinked.Should().BeFalse("the hop that reached it now follows a current link");
            laterTest.Via.Should().Be(testVia);
        }
        finally { await store.ImportAsync(NoCodeMap("git:clear"), at.AddSeconds(9)); }
    }

    [Fact]
    public async Task ALinkWhoseTargetKeyHasNoLiveVersionIsLeftOutOfTheHopAndCountedAsDangling()
    {
        // A work item links to two code-map files; a later code map no longer holds one, so its key has no live version, and the
        // item, unchanged, still links to it. Work declares WORK_CHANGED incoming only; read outgoing here, from the item, which
        // is where a dangling link shows (an incoming hop starts from a live record).
        var store = new Neo4jModuleSnapshotStore(fixture.TransactionRunner);
        var at = DateTimeOffset.UtcNow.AddMinutes(80);
        await store.ImportAsync(NoCodeMap("git:clear"), at.AddSeconds(-2));
        await store.ImportAsync(NoWork("git:clear"), at.AddSeconds(-1));
        try
        {
            static ModuleSnapshot Map(string source, bool withGone)
            {
                var builder = new CodeMapBuilder();
                builder.AddProject("src/Dangling/Dangling.csproj", "Acme.Dangling");
                builder.AddFile("src/Dangling/Keep.cs", "src/Dangling/Dangling.csproj");
                if (withGone) builder.AddFile("src/Dangling/Gone.cs", "src/Dangling/Dangling.csproj");
                return builder.Build(source);
            }
            await store.ImportAsync(Map("git:dangling-one", withGone: true), at);
            const string ledger = """{"tasks":[{"id":"DANGLING-1","task":"Touch both files","status":"Done","notes":"CO-2c."}]}""";
            var work = WorkLedgerReader.Read(ledger, "tasks.json", [new WorkCommit("g1", "DANGLING-1: both", ["src/Dangling/Keep.cs", "src/Dangling/Gone.cs"])],
                "git:dangling-one");
            (await store.ImportAsync(work, at.AddSeconds(1))).LinksLeftOut.Should().Be(0);
            await store.ImportAsync(Map("git:dangling-two", withGone: false), at.AddSeconds(2));

            var outward = WorkPack.Manifest with
            {
                Traversal = WorkPack.Manifest.Traversal! with
                {
                    Relations = [.. WorkPack.Manifest.Traversal.Relations.Where(r => r.Name == WorkPack.Changed).Select(r => r with { Direction = ModuleTraversalDirection.Out })],
                },
            };
            var resolution = ModuleResolver.Resolve([AgentMemory.Packs.Intent.IntentPack.Manifest, CodeMapPack.Manifest, outward], ModuleSet.SdkVersion, ModuleHostFeatures.Known);
            resolution.Succeeded.Should().BeTrue(string.Join("; ", resolution.Errors));
            using var services = Policies();
            var reader = new Neo4jModuleTraversalReader(fixture.TransactionRunner, resolution, services.GetRequiredService<IMemoryIsolationPolicy>());
            var context = new ModuleReadContext { Scope = MemoryScope.For("lea") };
            var item = (await fixture.TransactionRunner.ReadAsync(async runner => await (await runner.RunAsync(
                "MATCH (n:WorkItem {item:'DANGLING-1'}) WHERE n.invalidated_at IS NULL RETURN n.id AS id")).ToListAsync(r => ValueExtensions.As<string>(r["id"]))))
                .Should().ContainSingle().Subject;

            var step = await reader.ExpandAsync(new() { ModuleId = WorkPack.Id, Relation = WorkPack.Changed, Direction = ModuleTraversalDirection.Out,
                FromIds = [item] }, context);
            step.Status.Should().Be(ModuleReadStatus.Succeeded, string.Join("; ", step.Errors));
            var kept = step.Edges.Should().ContainSingle("the removed file is not reached").Subject;
            kept.To.Properties["path"].GetString().Should().Be("src/Dangling/Keep.cs");
            kept.State.Should().Be(ModuleLinkState.Current, "the kept file did not change");
            step.DanglingLinks.Should().Be(1, "the link to the removed file is counted, not silently dropped");
            step.Errors.Should().BeEmpty();

            // A scope that cannot see the shared records counts nothing.
            (await reader.ExpandAsync(new() { ModuleId = WorkPack.Id, Relation = WorkPack.Changed, Direction = ModuleTraversalDirection.Out,
                FromIds = [item] }, new ModuleReadContext { Scope = MemoryScope.For("lea", includeShared: false) })).DanglingLinks.Should().Be(0);
        }
        finally
        {
            await store.ImportAsync(NoWork("git:clear"), at.AddSeconds(3));
            await store.ImportAsync(NoCodeMap("git:clear"), at.AddSeconds(4));
        }
    }
}
