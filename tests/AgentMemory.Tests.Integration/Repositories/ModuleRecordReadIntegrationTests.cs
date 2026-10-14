#pragma warning disable AMEXT001
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Reading;
using AgentMemory.Neo4j.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Repositories;

public sealed partial class ModuleRecordWriteIntegrationTests
{
    [Fact]
    public async Task ReadReturnsCurrentAndExactHistoricalVersionsWithoutSubstitutingUnsupportedClocks()
    {
        var r = await Ready("ReadHistory"); var batch = Batch(r); var store = new Neo4jModuleStore(fixture.TransactionRunner, r);
        var first = (await store.WriteAsync(batch)).Single();
        var second = (await store.WriteAsync(Batch(r, "changed") with { AtUtc = batch.AtUtc.AddSeconds(2) })).Single();
        using var services = Policies(); var reader = Reader(r, services); var query = ReadQuery(r);
        (await reader.FindAsync(query, ReadContext())).Records.Single().Id.Should().Be(second.RecordId);
        var historical = await reader.FindAsync(query, ReadContext() with { SystemAsOfUtc = batch.AtUtc.AddSeconds(1) });
        historical.Records.Single().Id.Should().Be(first.RecordId);
        historical.Records.Single().Properties["text"].GetString().Should().Be("lesson");
        historical.Records.Single().Source.MessageIds.Should().Equal("ReadHistory-m1");
        (await reader.FindAsync(query, ReadContext() with { SystemAsOfUtc = batch.AtUtc.AddSeconds(-1) })).Records.Should().BeEmpty();
        (await reader.FindAsync(query, ReadContext() with { SystemAsOfUtc = batch.AtUtc.AddSeconds(2) })).Records.Single().Id.Should().Be(second.RecordId);
        var unsupported = await reader.FindAsync(query, ReadContext() with { ValidAsOfUtc = batch.AtUtc });
        unsupported.Status.Should().Be(ModuleReadStatus.NotApplicable); unsupported.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadUsesActualStrictPolicyBeforeDatabaseAndRefusesForeignOwner()
    {
        var r = await Ready("ReadIsolation"); await new Neo4jModuleStore(fixture.TransactionRunner, r).WriteAsync(Batch(r));
        using var services = Policies(); var counted = new ReadCounter(fixture.TransactionRunner);
        var reader = new Neo4jModuleReader(counted, r, r.Modules.Single().Id, services.GetRequiredService<IMemoryIsolationPolicy>());
        (await reader.FindAsync(ReadQuery(r), new())).Status.Should().Be(ModuleReadStatus.Refused); counted.Calls.Should().Be(0);
        (await reader.FindAsync(ReadQuery(r), ReadContext() with { Scope = MemoryScope.For("other") })).Records.Should().BeEmpty();
        (await reader.FindAsync(ReadQuery(r), ReadContext())).Records.Should().ContainSingle();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var before = counted.Calls; var action = () => reader.FindAsync(ReadQuery(r), ReadContext(), cancelled.Token);
        await action.Should().ThrowAsync<OperationCanceledException>(); counted.Calls.Should().Be(before);
    }

    [Fact]
    public async Task SharedRecordsRespectIncludeSharedAndGlobalReadSemantics()
    {
        var r = await Ready("ReadShared", shared: true); var batch = Batch(r);
        batch = batch with { Provider = batch.Provider with { Trigger = AgentMemory.Extensibility.Ingestion.IngestionTrigger.OnRequest },
            Request = batch.Request with { Trigger = AgentMemory.Extensibility.Ingestion.IngestionTrigger.OnRequest, ProviderIds = [batch.Provider.Id] } };
        await new Neo4jModuleStore(fixture.TransactionRunner, r).WriteAsync(batch);
        using var services = Policies(); var reader = Reader(r, services);
        (await reader.FindAsync(ReadQuery(r), ReadContext() with { Scope = MemoryScope.For("other") })).Records.Should().ContainSingle();
        (await reader.FindAsync(ReadQuery(r), ReadContext() with { Scope = MemoryScope.For("owner", false) })).Records.Should().BeEmpty();
        using var global = Policies(strict: false);
        (await Reader(r, global).FindAsync(ReadQuery(r), new())).Records.Should().ContainSingle();
    }

    [Fact]
    public async Task RequiredAnchorLossIsFilteredBeforeLimitAndNeverLeaksForeignLinks()
    {
        var r = await Ready("ReadAnchors", links: true); var store = new Neo4jModuleStore(fixture.TransactionRunner, r);
        await Execute("CREATE (:Entity {id:'read-dead',owner_id:'owner'}), (:Entity {id:'read-live',owner_id:'owner'})");
        var first = WithLink(Batch(r, "a"), "READ_ANCHORS_ABOUT", "read-dead");
        await store.WriteAsync(first);
        var second = WithLink(Batch(r, "b"), "READ_ANCHORS_ABOUT", "read-live");
        second = second with { Proposals = [second.Proposals[0] with { Proposal = second.Proposals[0].Proposal with { Key = "second" } }] };
        await store.WriteAsync(second);
        await Execute("MATCH (e:Entity {id:'read-dead'}) DETACH DELETE e");
        using var services = Policies(); var reader = Reader(r, services);
        var query = ReadQuery(r) with { Limit = 1, OrderBy = new() { Property = "text" } };
        var result = await reader.FindAsync(query, ReadContext());
        result.Records.Single().Key.Should().Be("second"); result.Records.Single().Links.Single().TargetId.Should().Be("read-live");
        await Execute("MATCH (e:Entity {id:'read-live'}) SET e.owner_id='foreign'");
        (await reader.FindAsync(query, ReadContext())).Records.Should().BeEmpty();
    }

    [Fact]
    public async Task HistoricalAnchorFollowsIdentityOnlyAfterTheMergeTime()
    {
        var r = await Ready("ReadMerge", links: true); var batch = Batch(r);
        await Execute("CREATE (:Entity {id:'read-merge-a',owner_id:'owner',created_at:datetime('2026-10-14T11:00:00Z')}), (:Entity {id:'read-merge-b',owner_id:'owner',created_at:datetime('2026-10-14T11:00:00Z')})");
        await new Neo4jModuleStore(fixture.TransactionRunner, r).WriteAsync(WithLink(batch, "READ_MERGE_ABOUT", "read-merge-a"));
        await Execute("MATCH (a:Entity {id:'read-merge-a'}) SET a.invalidated_at=datetime('2026-10-14T12:00:02Z'), a.merged_into='read-merge-b'");
        using var services = Policies(); var reader = Reader(r, services);
        (await reader.FindAsync(ReadQuery(r), ReadContext())).Records.Single().Links.Single().TargetId.Should().Be("read-merge-b");
        (await reader.FindAsync(ReadQuery(r), ReadContext() with { SystemAsOfUtc = batch.AtUtc.AddSeconds(1) })).Records.Single().Links.Single().TargetId.Should().Be("read-merge-a");
        (await reader.FindAsync(ReadQuery(r), ReadContext() with { SystemAsOfUtc = batch.AtUtc.AddSeconds(2) })).Records.Single().Links.Single().TargetId.Should().Be("read-merge-b");
    }

    [Fact]
    public async Task DeclaredDependencyReadsUseRealStoreAndUndeclaredReadsNeverReachDatabase()
    {
        var target = Resolve("ReadDependency").Modules.Single();
        var caller = Resolve("ReadCaller").Modules.Single() with
        { Modules = new Dictionary<string, ModuleVersionRange> { [target.Id] = ModuleVersionRange.Parse("[1.0,2.0)") } };
        var stranger = Resolve("ReadStranger").Modules.Single();
        var r = ModuleResolver.Resolve([target, caller, stranger], ModuleVersion.Parse("0.1.0"), new HashSet<string> { "ModuleSchema" });
        r.Succeeded.Should().BeTrue();
        await Seed("ReadDependency", "owner");
        await new AgentMemory.Extensibility.Neo4j.Migrations.ModuleMigrator(fixture.TransactionRunner, r).ApplyAsync("integration");
        await new Neo4jModuleStore(fixture.TransactionRunner, r).WriteAsync(Batch(Resolve("ReadDependency")) with { ManifestFingerprint = r.ManifestFingerprint! });
        using var services = Policies(); var counted = new ReadCounter(fixture.TransactionRunner);
        var reader = new Neo4jModuleReader(counted, r, caller.Id, services.GetRequiredService<IMemoryIsolationPolicy>());
        var query = new ModuleQuery { ModuleId = target.Id, Type = "ReadDependencyItem", Limit = 10 };
        (await reader.FindAsync(query, ReadContext())).Records.Single().ModuleId.Should().Be(target.Id);
        (await reader.FindAsync(query, ReadContext() with { Scope = MemoryScope.For("foreign") })).Records.Should().BeEmpty();
        var before = counted.Calls;
        (await reader.FindAsync(query with { ModuleId = stranger.Id, Type = "ReadStrangerItem" }, ReadContext())).Status.Should().Be(ModuleReadStatus.Refused);
        counted.Calls.Should().Be(before);
    }

    [Fact]
    public async Task SharedViewsExecuteTypedFiltersOrderingAndRenderingAgainstStoredRecords()
    {
        var module = Resolve("ReadView").Modules.Single(); var type = module.Ontology!.Types.Single();
        type = type with { Render = "{{{text}}} {count:D3} {due:yyyy-MM-dd}", Properties = new Dictionary<string, ModuleProperty>(type.Properties)
        { ["count"] = new() { Type = "int" }, ["due"] = new() { Type = "datetime" }, ["day"] = new() { Type = "date" }, ["active"] = new() { Type = "bool" }, ["session"] = new() { Type = "string" } } };
        module = module with { Ontology = module.Ontology with { Types = [type] }, Views = [new() { Id = "due", Type = type.Name, Limit = 2,
            Where = [Filter("due", "<=", "$now"), Filter("session", "=", "$session"), Filter("active", "=", true), Filter("day", "in", new[] { "2026-10-14" })],
            OrderBy = new() { Property = "count", Descending = true } }] };
        var r = ModuleResolver.Resolve([module], ModuleVersion.Parse("0.1.0"), new HashSet<string> { "ModuleSchema" }); r.Succeeded.Should().BeTrue();
        await Seed("ReadView", "owner"); await new AgentMemory.Extensibility.Neo4j.Migrations.ModuleMigrator(fixture.TransactionRunner, r).ApplyAsync("integration");
        var store = new Neo4jModuleStore(fixture.TransactionRunner, r);
        for (var n = 1; n <= 4; n++)
        {
            var batch = Batch(r, "item" + n);
            batch = batch with { Proposals = [batch.Proposals[0] with { Proposal = batch.Proposals[0].Proposal with { Key = "item" + n,
                Properties = new Dictionary<string, System.Text.Json.JsonElement>(batch.Proposals[0].Proposal.Properties)
                { ["count"] = System.Text.Json.JsonSerializer.SerializeToElement(n), ["due"] = System.Text.Json.JsonSerializer.SerializeToElement(batch.AtUtc.AddDays(n == 4 ? 1 : 0)),
                    ["day"] = System.Text.Json.JsonSerializer.SerializeToElement("2026-10-14"), ["active"] = System.Text.Json.JsonSerializer.SerializeToElement(true),
                    ["session"] = System.Text.Json.JsonSerializer.SerializeToElement("teaching") } } }] };
            (await store.WriteAsync(batch)).Single().Status.Should().Be(AgentMemory.Extensibility.Ingestion.ProposalWriteStatus.Stored);
        }
        using var services = Policies(); var reader = Reader(r, services);
        IModuleViews views = new ModuleViews(r, reader);
        var context = ReadContext() with { SessionId = "teaching", SystemAsOfUtc = DateTimeOffset.Parse("2026-10-14T12:00:01Z") };
        var result = await views.EvaluateAsync("due", context);
        result.Status.Should().Be(ModuleReadStatus.Succeeded);
        result.Items.Select(x => x.Record.Key).Should().Equal("item3", "item2");
        result.Items.Select(x => x.Text).Should().Equal("{item3} 003 2026-10-14", "{item2} 002 2026-10-14");
        (await views.EvaluateAsync("due", context with { SessionId = "fresh" })).Items.Should().BeEmpty();
        (await views.EvaluateAsync("due", context with { ValidAsOfUtc = context.SystemAsOfUtc })).Status.Should().Be(ModuleReadStatus.NotApplicable);
        var query = ReadQuery(r) with { Where = [Filter("count", ">=", 2), Filter("count", "!=", 4)], OrderBy = new() { Property = "count" } };
        (await reader.FindAsync(query, ReadContext())).Records.Select(x => x.Key).Should().Equal("item2", "item3");
        (await reader.FindAsync(query with { Where = [Filter("count", "=", (object?)null)] }, ReadContext())).Records.Should().BeEmpty();
        (await reader.FindAsync(query with { Where = [Filter("count", "!=", (object?)null)] }, ReadContext())).Records.Should().HaveCount(4);
    }

    private static ModuleViewFilter Filter(string property, string op, object? value) => new()
    { Property = property, Operator = op, Value = System.Text.Json.JsonSerializer.SerializeToElement(value) };

    private static ServiceProvider Policies(bool strict = true)
    {
        var services = new ServiceCollection(); services.AddLogging();
        services.AddAgentMemoryCore(o => o.Isolation.Mode = strict ? MemoryIsolationMode.StrictMultiTenant : MemoryIsolationMode.SingleTenant);
        return services.BuildServiceProvider();
    }
    private Neo4jModuleReader Reader(ModuleResolution r, ServiceProvider services) => new(fixture.TransactionRunner, r, r.Modules.Single().Id, services.GetRequiredService<IMemoryIsolationPolicy>());
    private static ModuleQuery ReadQuery(ModuleResolution r) => new() { ModuleId = r.Modules.Single().Id, Type = r.Modules.Single().Ontology!.Types.Single().Name, Limit = 10 };
    private static ModuleReadContext ReadContext() => new() { Scope = MemoryScope.For("owner") };
    private sealed class ReadCounter(INeo4jTransactionRunner inner) : INeo4jTransactionRunner
    {
        public int Calls { get; private set; }
        public Task<T> ReadAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default) { Calls++; return inner.ReadAsync(work, cancellationToken); }
        public Task ReadAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default) { Calls++; return inner.ReadAsync(work, cancellationToken); }
        public Task<T> WriteAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Read path must not write.");
        public Task WriteAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Read path must not write.");
    }
}
