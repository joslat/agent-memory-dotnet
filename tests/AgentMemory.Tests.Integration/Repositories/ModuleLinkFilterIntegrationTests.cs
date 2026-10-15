#pragma warning disable AMEXT001
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
    public async Task LinkFilterPrecedesLimitAcrossStoragePagesAndSharedViewArgumentsReachStorage()
    {
        var module = Resolve("ReadFilterPage", links: true).Modules.Single();
        module = module with { Views = [new() { Id = "items", Type = "ReadFilterPageItem", Limit = 3,
            OrderBy = new() { Property = "text" } }] };
        var r = ModuleResolver.Resolve([module], ModuleVersion.Parse("0.1.0"), new HashSet<string> { "ModuleSchema" });
        r.Succeeded.Should().BeTrue(); await Seed("ReadFilterPage", "owner");
        await new ModuleMigrator(fixture.TransactionRunner, r).ApplyAsync("integration");
        await Execute("CREATE (:Entity {id:'filter-page-other',owner_id:'owner'}), (:Entity {id:'filter-page-wanted',owner_id:'owner'})");
        var batch = Batch(r);
        var proposals = Enumerable.Range(0, 129).Select(i =>
        {
            var item = WithLink(Batch(r, i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture)),
                "READ_FILTER_PAGE_ABOUT", i == 128 ? "filter-page-wanted" : "filter-page-other").Proposals.Single();
            return item with { Index = i, Proposal = item.Proposal with { Key = "item-" + i } };
        }).ToArray();
        var written = await new Neo4jModuleStore(fixture.TransactionRunner, r).WriteAsync(batch with { Proposals = proposals });
        written.Should().HaveCount(129).And.OnlyContain(x => x.Status == ProposalWriteStatus.Stored);
        using var services = Policies(); var reader = Reader(r, services);
        var query = ReadQuery(r) with { Limit = 1, OrderBy = new() { Property = "text" },
            Links = [new() { Relation = "READ_FILTER_PAGE_ABOUT", TargetId = "filter-page-wanted" }] };
        var result = await reader.FindAsync(query, ReadContext());
        result.Status.Should().Be(ModuleReadStatus.Succeeded);
        result.Records.Single().Key.Should().Be("item-128");
        result.Records.Single().Id.Should().Be(written[128].RecordId);
        IModuleViews views = new ModuleViews(r, reader);
        var rendered = await views.EvaluateWithArgumentsAsync("items", new() { Key = "item-128", Links = query.Links, Limit = 1 }, ReadContext());
        rendered.Status.Should().Be(ModuleReadStatus.Succeeded);
        rendered.Items.Single().Record.Id.Should().Be(written[128].RecordId);
        rendered.Items.Single().Text.Should().Be("text=128");
        rendered.Items.Single().Record.Source.MessageIds.Should().Equal("ReadFilterPage-m1");
        (await reader.FindAsync(query with { Links = [] }, ReadContext())).Records.Single().Key.Should().Be("item-0");
        (await reader.FindAsync(query with { Links = [.. query.Links,
            new() { Relation = "READ_FILTER_PAGE_ABOUT", TargetId = "filter-page-other" }] }, ReadContext())).Records.Should().BeEmpty();
    }

    [Fact]
    public async Task LinkFiltersResolveMergedIdentitiesAtTheRequestedClockWithoutFollowingForeignAliases()
    {
        var r = await Ready("ReadFilterMerge", links: true); var batch = Batch(r);
        await Execute("CREATE (:Entity {id:'filter-merge-old',owner_id:'owner',created_at:datetime('2026-10-14T11:00:00Z')}), (:Entity {id:'filter-merge-new',owner_id:'owner',created_at:datetime('2026-10-14T11:00:00Z')}), (:Entity {id:'filter-merge-foreign',owner_id:'foreign',merged_into:'filter-merge-new'})");
        var write = await new Neo4jModuleStore(fixture.TransactionRunner, r).WriteAsync(WithLink(batch, "READ_FILTER_MERGE_ABOUT", "filter-merge-old"));
        write.Single().Status.Should().Be(ProposalWriteStatus.Stored);
        await Execute("MATCH (a:Entity {id:'filter-merge-old'}) SET a.invalidated_at=datetime('2026-10-14T12:00:02Z'), a.merged_into='filter-merge-new'");
        using var services = Policies(); var reader = Reader(r, services);
        ModuleQuery Query(string target) => ReadQuery(r) with { Links = [new() { Relation = "READ_FILTER_MERGE_ABOUT", TargetId = target }] };
        foreach (var id in new[] { "filter-merge-old", "filter-merge-new" })
            (await reader.FindAsync(Query(id), ReadContext())).Records.Single().Id.Should().Be(write.Single().RecordId);
        var before = ReadContext() with { SystemAsOfUtc = batch.AtUtc.AddSeconds(1) };
        (await reader.FindAsync(Query("filter-merge-old"), before)).Records.Should().ContainSingle();
        (await reader.FindAsync(Query("filter-merge-new"), before)).Records.Should().BeEmpty();
        (await reader.FindAsync(Query("filter-merge-new"), before with { SystemAsOfUtc = batch.AtUtc.AddSeconds(2) })).Records.Should().ContainSingle();
        foreach (var id in new[] { "filter-merge-foreign", "filter-merge-missing" })
            (await reader.FindAsync(Query(id), ReadContext())).Records.Should().BeEmpty();
        (await reader.FindAsync(Query("filter-merge-old"), ReadContext() with { ValidAsOfUtc = batch.AtUtc })).Status.Should().Be(ModuleReadStatus.NotApplicable);
        await Execute("MATCH (a:Entity {id:'filter-merge-new'}) SET a.owner_id='foreign'");
        (await reader.FindAsync(Query("filter-merge-old"), ReadContext())).Records.Should().BeEmpty();
    }

    [Fact]
    public async Task LinkFiltersDoNotExposeSharedTargetsWhenSharedVisibilityIsDisabled()
    {
        var r = await Ready("ReadFilterShared", links: true);
        await Execute("CREATE (:Entity {id:'filter-shared-anchor'})");
        var write = await new Neo4jModuleStore(fixture.TransactionRunner, r).WriteAsync(WithLink(Batch(r), "READ_FILTER_SHARED_ABOUT", "filter-shared-anchor"));
        write.Single().Status.Should().Be(ProposalWriteStatus.Stored);
        using var services = Policies(); var reader = Reader(r, services);
        var query = ReadQuery(r) with { Links = [new() { Relation = "READ_FILTER_SHARED_ABOUT", TargetId = "filter-shared-anchor" }] };
        (await reader.FindAsync(query, ReadContext())).Records.Should().ContainSingle();
        (await reader.FindAsync(query, ReadContext() with { Scope = AgentMemory.Abstractions.Options.MemoryScope.For("owner", false) })).Records.Should().BeEmpty();
        (await reader.FindAsync(query, ReadContext() with { Scope = AgentMemory.Abstractions.Options.MemoryScope.For("foreign") })).Records.Should().BeEmpty();
    }
}
