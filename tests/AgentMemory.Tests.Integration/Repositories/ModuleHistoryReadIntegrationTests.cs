#pragma warning disable AMEXT001
using AgentMemory.Abstractions.Options;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Migrations;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Reading;
using FluentAssertions;

namespace AgentMemory.Tests.Integration.Repositories;

public sealed partial class ModuleRecordWriteIntegrationTests
{
    [Fact]
    public async Task HistoryListsNumericVersionsAtEqualTimestampsWithCutoffInvalidationAndRetainedProvenance()
    {
        var r = await Ready("ReadVersions"); var store = new Neo4jModuleStore(fixture.TransactionRunner, r);
        var batch = Batch(r); var ids = new List<string>();
        for (var version = 1; version <= 13; version++)
        {
            var write = await store.WriteAsync(Batch(r, "v" + version) with { AtUtc = batch.AtUtc.AddSeconds(version == 13 ? 1 : 0) });
            write.Single().Status.Should().Be(ProposalWriteStatus.Stored); ids.Add(write.Single().RecordId!);
        }
        using var services = Policies(); var reader = Reader(r, services);
        var query = ReadQuery(r) with { Selection = ModuleRecordSelection.History, Key = "lesson", Limit = 3 };
        (await reader.FindAsync(query, ReadContext())).Records.Select(x => x.Id).Should().Equal(ids[12], ids[11], ids[10]);
        var cutoff = ReadContext() with { SystemAsOfUtc = batch.AtUtc };
        var history = await reader.FindAsync(query, cutoff);
        history.Status.Should().Be(ModuleReadStatus.Succeeded);
        history.Records.Select(x => x.Id).Should().Equal(ids[11], ids[10], ids[9]);
        history.Records[0].InvalidatedAtUtc.Should().Be(batch.AtUtc.AddSeconds(1));
        (await reader.FindAsync(query, cutoff with { SystemAsOfUtc = batch.AtUtc.AddTicks(-1) })).Records.Should().BeEmpty();
        (await reader.FindAsync(query with { Selection = ModuleRecordSelection.Snapshot }, cutoff)).Records.Single().Id.Should().Be(ids[11]);
        var invalid = batch with { AtUtc = batch.AtUtc.AddSeconds(2), Proposals = [batch.Proposals.Single() with
            { Proposal = batch.Proposals.Single().Proposal with { Kind = ProposalKind.Invalidate, Properties = new Dictionary<string, System.Text.Json.JsonElement>() } }] };
        (await store.WriteAsync(invalid)).Single().Status.Should().Be(ProposalWriteStatus.Invalidated);
        (await reader.FindAsync(ReadQuery(r), ReadContext())).Records.Should().BeEmpty();
        await Execute("MATCH (m:Message {id:'ReadVersions-m1'}) DETACH DELETE m");
        var retained = await reader.FindAsync(query with { Limit = 100 }, ReadContext());
        retained.Records.Select(x => x.Id).Should().Equal(ids.AsEnumerable().Reverse());
        retained.Records.Should().OnlyContain(x => x.Source.MessageIds.Single() == "ReadVersions-m1" && x.Source.Excerpt == "lesson");
        (await reader.FindAsync(query, ReadContext() with { Scope = MemoryScope.For("foreign") })).Records.Should().BeEmpty();
        (await reader.FindAsync(query, ReadContext() with { ValidAsOfUtc = batch.AtUtc })).Status.Should().Be(ModuleReadStatus.NotApplicable);
    }

    [Fact]
    public async Task HistoryViewsUseRuntimeKeyAndQueryTimeAnchorsWithoutResurrectingForeignTargets()
    {
        var module = Resolve("ReadHistoryView", links: true).Modules.Single();
        module = module with { Views = [new() { Id = "versions", Type = "ReadHistoryViewItem", Limit = 5, Selection = ModuleRecordSelection.History }] };
        var r = ModuleResolver.Resolve([module], ModuleVersion.Parse("0.1.0"), new HashSet<string> { "ModuleSchema" });
        r.Succeeded.Should().BeTrue(); await Seed("ReadHistoryView", "owner");
        await new ModuleMigrator(fixture.TransactionRunner, r).ApplyAsync("integration");
        await Execute("CREATE (:Entity {id:'history-view-target',owner_id:'owner',created_at:datetime('2026-10-14T11:00:00Z')})");
        var store = new Neo4jModuleStore(fixture.TransactionRunner, r); var batch = WithLink(Batch(r), "READ_HISTORY_VIEW_ABOUT", "history-view-target");
        var first = (await store.WriteAsync(batch)).Single(); first.Status.Should().Be(ProposalWriteStatus.Stored);
        var second = (await store.WriteAsync(Batch(r, "changed") with { AtUtc = batch.AtUtc.AddSeconds(1) })).Single();
        second.Status.Should().Be(ProposalWriteStatus.Stored);
        using var services = Policies(); IModuleViews views = new ModuleViews(r, Reader(r, services));
        var arguments = new ModuleViewArguments { Key = "lesson", Links = [new() { Relation = "READ_HISTORY_VIEW_ABOUT", TargetId = "history-view-target" }] };
        var result = await views.EvaluateWithArgumentsAsync("versions", arguments, ReadContext());
        result.Status.Should().Be(ModuleReadStatus.Succeeded);
        result.Items.Select(x => x.Record.Id).Should().Equal(second.RecordId, first.RecordId);
        (await views.EvaluateWithArgumentsAsync("versions", arguments, ReadContext() with { SystemAsOfUtc = batch.AtUtc })).Items.Single().Record.Id.Should().Be(first.RecordId);
        (await views.EvaluateAsync("versions", ReadContext())).Status.Should().Be(ModuleReadStatus.Refused);
        await Execute("MATCH (e:Entity {id:'history-view-target'}) SET e.invalidated_at=datetime('2026-10-14T12:00:02Z')");
        (await views.EvaluateWithArgumentsAsync("versions", arguments, ReadContext())).Items.Should().BeEmpty();
        (await views.EvaluateWithArgumentsAsync("versions", arguments, ReadContext() with { SystemAsOfUtc = batch.AtUtc })).Items.Should().ContainSingle();
        await Execute("MATCH (e:Entity {id:'history-view-target'}) SET e.owner_id='foreign'");
        (await views.EvaluateWithArgumentsAsync("versions", arguments, ReadContext() with { SystemAsOfUtc = batch.AtUtc })).Items.Should().BeEmpty();
    }
}
