#pragma warning disable AMEXT001
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Context.Traversal;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Extensibility.Reading;
using AgentMemory.Packs.CodeMap;
using AgentMemory.Packs.Intent;
using AgentMemory.Packs.Work;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// The coding ontology's slice 1 relations on a real Neo4j (CO-2b): a test, a work item and a child requirement each store the
/// link their own source asserts into the intent pack's requirements; a walk from the file in hand climbs to the requirement,
/// labelled why, with its reason path as before; and a requirement that changes leaves those links on its older version, so they
/// read as suspect (CO-2c) until their sources are written again.
/// </summary>
public sealed partial class ModuleRecordWriteIntegrationTests
{
    private const string TraceTest = "scip-dotnet nuget Acme.Trace.Tests . Acme/Trace/Tests/TraceStoreTests#Remove_keeps_history().";

    private static ModuleSnapshot NoIntent(string source) => new() { Module = IntentPack.Manifest, Source = source, Records = [] };

    private static ModuleSnapshot TraceIntent(string root, string source, string keep, string exact)
    {
        File.WriteAllText(Path.Combine(root, "REQUIREMENTS.md"), $"- TRACE-1: {exact}\n- TRACE-12: {keep} (parent: TRACE-1)\n");
        var snapshot = IntentReader.Read(root, ["REQUIREMENTS.md"], source, out var skipped);
        skipped.Should().BeEmpty();
        return snapshot;
    }

    private static ModuleSnapshot TraceMap(string source)
    {
        var builder = new CodeMapBuilder();
        builder.AddProject("src/Trace/Trace.csproj", "Acme.Trace");
        builder.AddFile("src/Trace/TraceStore.cs", "src/Trace/Trace.csproj");
        builder.AddFile("tests/TraceStoreTests.cs", null);
        builder.AddTest(TraceTest, "TraceStoreTests.Remove_keeps_history", "tests/TraceStoreTests.cs", 9, "xunit", []);
        builder.AddValidates(TraceTest, ["TRACE-12", "TRACE-404"]);
        return builder.Build(source);
    }

    [Fact]
    public async Task LinksIntoARequirementAreStoredByTheirSourcesWalkedAsWhyAndSuspectOnceTheRequirementChanges()
    {
        var store = new Neo4jModuleSnapshotStore(fixture.TransactionRunner);
        var at = DateTimeOffset.UtcNow.AddMinutes(100);
        var root = Directory.CreateTempSubdirectory("co2b-").FullName;
        await store.ImportAsync(NoWork("git:clear"), at.AddSeconds(-3));
        await store.ImportAsync(NoCodeMap("git:clear"), at.AddSeconds(-2));
        await store.ImportAsync(NoIntent("git:clear"), at.AddSeconds(-1));
        try
        {
            (await store.ImportAsync(TraceIntent(root, "git:one", "The store shall keep history.", "The store shall be exact."), at))
                .Should().Be(new ModuleSnapshotResult { ModuleId = IntentPack.Id, Source = "git:one", Created = 2 });
            (await store.ImportAsync(TraceMap("git:one"), at.AddSeconds(1))).LinksLeftOut.Should().Be(1, "TRACE-404 is no requirement the intent pack holds");
            const string ledger = """{"tasks":[{"id":"TRACE-A1","task":"Keep history (TRACE-12)","status":"Done"}]}""";
            var work = WorkLedgerReader.Read(ledger, "tasks.json", [new WorkCommit("t1", "TRACE-A1: keep history", ["src/Trace/TraceStore.cs", "tests/TraceStoreTests.cs"])], "git:one",
                new WorkLedgerOptions { Requirements = new HashSet<string> { "TRACE-12" } });
            (await store.ImportAsync(work, at.AddSeconds(2))).LinksLeftOut.Should().Be(0);

            var resolution = ModuleResolver.Resolve([IntentPack.Manifest, CodeMapPack.Manifest, WorkPack.Manifest], ModuleSet.SdkVersion, ModuleHostFeatures.Known);
            resolution.Succeeded.Should().BeTrue(string.Join("; ", resolution.Errors));
            using var services = Policies();
            var reader = new Neo4jModuleTraversalReader(fixture.TransactionRunner, resolution, services.GetRequiredService<IMemoryIsolationPolicy>());
            var layers = new Dictionary<string, int> { [IntentPack.Id] = 1, [WorkPack.Id] = 4, [CodeMapPack.Id] = 5 };
            var request = new ContextRequest { SessionId = "s", Owner = "lea", Query = "", Anchors = [ContextAnchor.Path("tests/TraceStoreTests.cs")] };
            const string requirementVia = "TraceStoreTests.cs → TraceStoreTests.Remove_keeps_history declared in it → validates TRACE-12";

            var fresh = await TraversalWalk.RunAsync(resolution, reader, request, CancellationToken.None, layers: layers);
            fresh.Problems.Should().BeEmpty();
            var requirement = fresh.Items.Single(i => i.Record.Key == "TRACE-12");
            requirement.Via.Should().Be(requirementVia);
            requirement.Toward.Should().Be(TraversalMeaning.Why);
            requirement.ChangedSinceLinked.Should().BeFalse();
            fresh.Items.Single(i => i.Record.Key == "TRACE-A1").Toward.Should().Be(TraversalMeaning.Why, "from code to the work that changed it");

            // A host that composes the Coding family gets its layer order from the composite, and the label on each item's
            // provenance; the item's text and reason are as before.
            await using (var host = Host(m => m.UseNeo4jStore().AddComposite<AgentMemory.Packs.Coding.CodingPack>(o => o.Disable(AgentMemory.Packs.Repository.RepositoryPack.Id))))
            {
                var envelope = await CompileAsync(host, request);
                var offered = envelope.Sections.SelectMany(s => s.Items).Where(i => i.Provenance?.RecordModuleId == IntentPack.Id).ToList();
                var item12 = offered.Should().ContainSingle(i => i.Provenance!.RecordKey == "TRACE-12").Subject;
                item12.Provenance!.ViaMeaning.Should().Be(TraversalMeaning.Why);
                item12.Provenance.Via.Should().Be(requirementVia);
                envelope.Sections.SelectMany(s => s.Items).Where(i => i.Provenance?.RecordModuleId == CodeMapPack.Id)
                    .Should().OnlyContain(i => i.Provenance!.ViaMeaning == null, "a test reached from its own file stays within the code layer");
            }

            // TRACE-12 is reworded. The test, the work item and its own parent link are not written again: the first two still
            // point at its first version (suspect); TRACE-12, written again, links to its parent's live version (current).
            (await store.ImportAsync(TraceIntent(root, "git:two", "The store shall keep history for seven years.", "The store shall be exact."), at.AddSeconds(3)))
                .Should().Be(new ModuleSnapshotResult { ModuleId = IntentPack.Id, Source = "git:two", Changed = 1, Unchanged = 1 });
            var changed = await TraversalWalk.RunAsync(resolution, reader, request, CancellationToken.None, layers: layers);
            var reworded = changed.Items.Single(i => i.Record.Key == "TRACE-12");
            reworded.ChangedSinceLinked.Should().BeTrue("the test's link points at the requirement's first version");
            reworded.Via.Should().Be(requirementVia, "the reason path stays as R-WALK-066 has it");
            reworded.Toward.Should().Be(TraversalMeaning.Why);

            var context = new ModuleReadContext { Scope = MemoryScope.For("lea") };
            async Task<string> Live(string label, string property, string value) => (await fixture.TransactionRunner.ReadAsync(async runner => await (await runner.RunAsync(
                $"MATCH (n:{label} {{{property}: $value}}) WHERE n.invalidated_at IS NULL RETURN n.id AS id", new { value })).ToListAsync(r => ValueExtensions.As<string>(r["id"]))))
                .Should().ContainSingle().Subject;
            var item = await Live("WorkItem", "item", "TRACE-A1");
            var implements = await reader.ExpandAsync(new() { ModuleId = WorkPack.Id, Relation = WorkPack.Implements, Direction = ModuleTraversalDirection.Out, FromIds = [item] }, context);
            implements.Edges.Should().ContainSingle().Which.State.Should().Be(ModuleLinkState.Suspect, "the item still links to the requirement's first version");
            var req12 = await Live("IntentRequirement", "reference", "TRACE-12");
            var validatedBy = await reader.ExpandAsync(new() { ModuleId = CodeMapPack.Id, Relation = CodeMapPack.Validates, Direction = ModuleTraversalDirection.In, FromIds = [req12] }, context);
            validatedBy.Edges.Should().ContainSingle().Which.State.Should().Be(ModuleLinkState.Suspect);
            async Task<ModuleLinkState> Parent() => (await reader.ExpandAsync(new() { ModuleId = IntentPack.Id, Relation = IntentPack.Decomposes,
                Direction = ModuleTraversalDirection.Out, FromIds = [await Live("IntentRequirement", "reference", "TRACE-12")] }, context)).Edges.Should().ContainSingle().Subject.State;
            (await Parent()).Should().Be(ModuleLinkState.Current, "TRACE-12 was written again, with its link to its parent's live version");

            // The parent changes: the child holds the link, so the child's link is suspect now (a changed parent flags its children).
            await store.ImportAsync(TraceIntent(root, "git:three", "The store shall keep history for seven years.", "The store shall be exact and complete."), at.AddSeconds(4));
            (await Parent()).Should().Be(ModuleLinkState.Suspect);

            // The work item is written again (a new commit names it): its link points at the requirement's live version again.
            var again = WorkLedgerReader.Read(ledger, "tasks.json", [new WorkCommit("t1", "TRACE-A1: keep history", ["src/Trace/TraceStore.cs", "tests/TraceStoreTests.cs"]), new WorkCommit("t2", "TRACE-A1: seven years", ["src/Trace/TraceStore.cs"])],
                "git:four", new WorkLedgerOptions { Requirements = new HashSet<string> { "TRACE-12" } });
            await store.ImportAsync(again, at.AddSeconds(5));
            (await reader.ExpandAsync(new() { ModuleId = WorkPack.Id, Relation = WorkPack.Implements, Direction = ModuleTraversalDirection.Out,
                FromIds = [await Live("WorkItem", "item", "TRACE-A1")] }, context)).Edges.Should().ContainSingle().Which.State.Should().Be(ModuleLinkState.Current);
        }
        finally
        {
            await store.ImportAsync(NoWork("git:clear"), at.AddSeconds(6));
            await store.ImportAsync(NoCodeMap("git:clear"), at.AddSeconds(7));
            await store.ImportAsync(NoIntent("git:clear"), at.AddSeconds(8));
            Directory.Delete(root, recursive: true);
        }
    }
}
