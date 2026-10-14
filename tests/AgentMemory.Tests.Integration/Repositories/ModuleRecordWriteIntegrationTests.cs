#pragma warning disable AMEXT001
using System.Text.Json;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Migrations;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Repositories;

[Trait("Category", "Integration")]
public sealed class ModuleRecordWriteIntegrationTests(Neo4jIntegrationFixture fixture) : IClassFixture<Neo4jIntegrationFixture>
{
    [Fact]
    public async Task PendingMigrationsRefuseWithoutApplyingDdlThenExplicitApplyMakesWritesReady()
    {
        var r = Resolve("StoreReady"); await Seed("StoreReady", "owner");
        var store = new Neo4jModuleStore(fixture.TransactionRunner, r);
        (await store.WriteAsync(Batch(r))).Single().Status.Should().Be(ProposalWriteStatus.Refused);
        (await Rows("MATCH (n:StoreReadyItem) RETURN n")).Should().BeEmpty();
        var plan = await new ModuleMigrator(fixture.TransactionRunner, r).PlanAsync();
        plan.Pending.Should().NotBeEmpty();
        await new ModuleMigrator(fixture.TransactionRunner, r).ApplyAsync("integration");
        (await store.WriteAsync(Batch(r))).Single().Status.Should().Be(ProposalWriteStatus.Stored);
        (await new ModuleMigrator(fixture.TransactionRunner, r).PlanAsync()).PendingBookkeeping.Should().BeEmpty();
    }

    [Fact]
    public async Task VersionsNoOpInvalidationAndProvenanceAreNonDestructive()
    {
        var r = await Ready("StoreHistory"); var store = new Neo4jModuleStore(fixture.TransactionRunner, r);
        var batch = Batch(r); var first = (await store.WriteAsync(batch)).Single();
        first.Status.Should().Be(ProposalWriteStatus.Stored);
        (await store.WriteAsync(batch)).Single().Status.Should().Be(ProposalWriteStatus.NoOp);
        var secondBatch = Batch(r, value: "changed") with { AtUtc = batch.AtUtc.AddSeconds(1) };
        var second = (await store.WriteAsync(secondBatch)).Single(); second.RecordId.Should().NotBe(first.RecordId);
        var rows = await Rows("MATCH (n:StoreHistoryItem) RETURN properties(n) AS n ORDER BY n.id");
        rows.Should().HaveCount(2);
        (await Rows("MATCH (:StoreHistoryItem)-[:SUPERSEDED_BY]->(:StoreHistoryItem) RETURN count(*) AS n")).Single()["n"].Should().Be(1L);
        var invalid = secondBatch with { AtUtc = batch.AtUtc.AddSeconds(2), Proposals = [new() { Index = 0, Proposal = secondBatch.Proposals[0].Proposal with { Kind = ProposalKind.Invalidate, Properties = new Dictionary<string, JsonElement>() } }] };
        (await store.WriteAsync(invalid)).Single().Status.Should().Be(ProposalWriteStatus.Invalidated);
        (await store.WriteAsync(invalid)).Single().Status.Should().Be(ProposalWriteStatus.NoOp);
        (await Rows("MATCH (n:StoreHistoryItem) WHERE n.invalidated_at IS NULL RETURN n")).Should().BeEmpty();
        await Execute("MATCH (m:Message {id:'StoreHistory-m1'}) DETACH DELETE m");
        var retained = (await Rows("MATCH (n:StoreHistoryItem) RETURN n.source_message_ids AS ids, n.source_excerpt AS excerpt")).ToArray();
        retained.Should().HaveCount(2); retained.Should().OnlyContain(x => (string)x["excerpt"] == "lesson");
        retained.SelectMany(x => ((System.Collections.IEnumerable)x["ids"]).Cast<string>()).Should().OnlyContain(id => id == "StoreHistory-m1");
    }

    [Fact]
    public async Task ActualSourceMembershipAndContentAreRequiredNotJustClaimedIds()
    {
        var r = await Ready("StoreSources"); var store = new Neo4jModuleStore(fixture.TransactionRunner, r);
        var good = Batch(r);
        var other = good with { Request = good.Request with { Owner = "other" } };
        (await store.WriteAsync(other)).Single().Status.Should().Be(ProposalWriteStatus.Refused);
        var changed = good with { Request = good.Request with { Messages = [good.Request.Messages[0] with { Text = "fabricated" }] } };
        (await store.WriteAsync(changed)).Single().Status.Should().Be(ProposalWriteStatus.Refused);
        var missing = good with { Request = good.Request with { Messages = [good.Request.Messages[0] with { Id = "missing" }] }, Proposals = [new() { Index = 0, Proposal = good.Proposals[0].Proposal with { Source = new() { MessageIds = ["missing"], Excerpt = "lesson" } } }] };
        (await store.WriteAsync(missing)).Single().Status.Should().Be(ProposalWriteStatus.Refused);
        (await Rows("MATCH (n:StoreSourcesItem) RETURN n")).Should().BeEmpty();
        (await store.WriteAsync(good)).Single().Status.Should().Be(ProposalWriteStatus.Stored);
    }

    [Fact]
    public async Task IndependentHostsSerializeSameKeyAndLeaveOneCurrentVersion()
    {
        var r = await Ready("StoreConcurrent");
        for (var wave = 0; wave < 3; wave++)
        {
            var prefix = "wave" + wave;
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
                new Neo4jModuleStore(fixture.TransactionRunner, r).WriteAsync(Batch(r, value: prefix + "-value" + i))));
            results.SelectMany(x => x).Should().OnlyContain(o => o.Status == ProposalWriteStatus.Stored);
        }
        (await Rows("MATCH (n:StoreConcurrentItem) RETURN count(*) AS n")).Single()["n"].Should().Be(24L);
        (await Rows("MATCH (n:StoreConcurrentItem) WHERE n.invalidated_at IS NULL RETURN count(*) AS n")).Single()["n"].Should().Be(1L);
        var keys = await Rows("MATCH (k:ModuleKey {module_id:'com.example.storeconcurrent'}) RETURN k.version AS version, k.current_id AS current");
        keys.Should().ContainSingle(); keys.Single()["version"].Should().Be(24L);
        (await Rows("MATCH (:StoreConcurrentItem)-[:SUPERSEDED_BY]->(:StoreConcurrentItem) RETURN count(*) AS n")).Single()["n"].Should().Be(23L);
    }

    [Fact]
    public async Task TransactionFailureRollsBackWholeBatchAndKeyCounters()
    {
        var r = await Ready("StoreRollback");
        var store = new Neo4jModuleStore(new FailingCommit(fixture.TransactionRunner), r);
        var batch = Batch(r); batch = batch with { Proposals = [batch.Proposals[0], batch.Proposals[0] with { Index = 1, Proposal = batch.Proposals[0].Proposal with { Key = "second" } }] };
        var action = () => store.WriteAsync(batch);
        await action.Should().ThrowAsync<IOException>();
        (await Rows("MATCH (n:StoreRollbackItem) RETURN n")).Should().BeEmpty();
        (await Rows("MATCH (k:ModuleKey {module_id:'com.example.storerollback'}) RETURN k")).Should().BeEmpty();
    }

    [Fact]
    public async Task RequiredLinksAreScopedCopiedReplacedAndFollowCoreMergeIdentity()
    {
        var r = await Ready("StoreLinks", links: true); var store = new Neo4jModuleStore(fixture.TransactionRunner, r);
        await Execute("CREATE (:Entity {id:'store-link-a',name:'Ana',aliases:['Dr Ana'],owner_id:'owner'}), (:Entity {id:'store-link-b',name:'Bea',owner_id:'owner'}), (:Entity {id:'store-link-foreign',name:'Foreign',owner_id:'other'})");
        var noLink = Batch(r);
        (await store.WriteAsync(noLink)).Single().Status.Should().Be(ProposalWriteStatus.Refused);
        var named = WithLink(noLink, "STORE_LINKS_ABOUT", "Dr Ana", LinkTargetKind.EntityName);
        (await store.WriteAsync(named)).Single().Status.Should().Be(ProposalWriteStatus.Stored);
        (await store.WriteAsync(Batch(r, value: "changed"))).Single().Status.Should().Be(ProposalWriteStatus.Stored);
        (await Rows("MATCH (n:StoreLinksItem)-[:STORE_LINKS_ABOUT]->(e:Entity) WHERE n.invalidated_at IS NULL RETURN e.id AS id")).Single()["id"].Should().Be("store-link-a");
        (await store.WriteAsync(WithLink(Batch(r, value: "foreign"), "STORE_LINKS_ABOUT", "store-link-foreign"))).Single().Status.Should().Be(ProposalWriteStatus.Refused);
        await Execute("MATCH (a:Entity {id:'store-link-a'}) SET a.merged_into='store-link-b', a.invalidated_at=datetime('2026-10-14T12:00:00Z')");
        (await store.WriteAsync(Batch(r, value: "merged"))).Single().Status.Should().Be(ProposalWriteStatus.Stored);
        (await Rows("MATCH (n:StoreLinksItem)-[:STORE_LINKS_ABOUT]->(e:Entity) WHERE n.invalidated_at IS NULL RETURN e.id AS id")).Single()["id"].Should().Be("store-link-b");
    }

    [Fact]
    public async Task SharedTypesRequireExplicitSelectionAndDoNotMutateCoreOrBaseLedger()
    {
        var r = await Ready("StoreShared", shared: true); var store = new Neo4jModuleStore(fixture.TransactionRunner, r);
        var before = await Rows("MATCH (n) WHERE n:Message OR n:Conversation OR n:Migration RETURN properties(n) AS row ORDER BY coalesce(n.id,n.version)");
        var batch = Batch(r);
        (await store.WriteAsync(batch)).Single().Status.Should().Be(ProposalWriteStatus.Refused);
        batch = batch with { Provider = batch.Provider with { Trigger = IngestionTrigger.OnRequest }, Request = batch.Request with { Trigger = IngestionTrigger.OnRequest, ProviderIds = [batch.Provider.Id] } };
        (await store.WriteAsync(batch)).Single().Status.Should().Be(ProposalWriteStatus.Stored);
        (await Rows("MATCH (n:StoreSharedItem) RETURN n.owner_id AS owner")).Single()["owner"].Should().BeNull();
        (await Rows("MATCH (n) WHERE n:Message OR n:Conversation OR n:Migration RETURN properties(n) AS row ORDER BY coalesce(n.id,n.version)"))
            .Select(x => x["row"]).Should().BeEquivalentTo(before.Select(x => x["row"]));
    }

    [Fact]
    public async Task ReassertCannotCreateOverlappingHistoryBeforePriorInvalidation()
    {
        var r = await Ready("StoreChronology"); var store = new Neo4jModuleStore(fixture.TransactionRunner, r);
        var batch = Batch(r); await store.WriteAsync(batch);
        var invalid = batch with { AtUtc = batch.AtUtc.AddSeconds(2), Proposals = [new() { Index = 0, Proposal = batch.Proposals[0].Proposal with { Kind = ProposalKind.Invalidate, Properties = new Dictionary<string, JsonElement>() } }] };
        await store.WriteAsync(invalid);
        var late = Batch(r, value: "later") with { AtUtc = batch.AtUtc.AddSeconds(1) };
        (await store.WriteAsync(late)).Single().Status.Should().Be(ProposalWriteStatus.Refused);
        (await Rows("MATCH (n:StoreChronologyItem) RETURN count(*) AS n")).Single()["n"].Should().Be(1L);
    }

    [Fact]
    public async Task CurrentRecordOwnerIsVerifiedEvenWhenKeyBookkeepingMatches()
    {
        var r = await Ready("StoreTampered"); var store = new Neo4jModuleStore(fixture.TransactionRunner, r);
        await store.WriteAsync(Batch(r));
        await Execute("MATCH (n:StoreTamperedItem) SET n.owner_id='other'");
        (await store.WriteAsync(Batch(r, value: "changed"))).Single().Status.Should().Be(ProposalWriteStatus.Refused);
        (await Rows("MATCH (n:StoreTamperedItem) RETURN count(*) AS n")).Single()["n"].Should().Be(1L);
    }

    [Fact]
    public async Task LaterRequiredLinkRefusalRollsBackEarlierProposalAndKeyRows()
    {
        var r = await Ready("StoreAtomic", links: true); var store = new Neo4jModuleStore(fixture.TransactionRunner, r);
        await Execute("CREATE (:Entity {id:'store-atomic-target',name:'target',owner_id:'owner'})");
        var batch = WithLink(Batch(r), "STORE_ATOMIC_ABOUT", "store-atomic-target");
        batch = batch with { Proposals = [batch.Proposals[0], new() { Index = 1, Proposal = Batch(r).Proposals[0].Proposal with { Key = "no-anchor" } }] };
        (await store.WriteAsync(batch)).Should().OnlyContain(o => o.Status == ProposalWriteStatus.Refused);
        (await Rows("MATCH (n:StoreAtomicItem) RETURN n")).Should().BeEmpty();
        (await Rows("MATCH (n:ModuleKey {module_id:'com.example.storeatomic'}) RETURN n")).Should().BeEmpty();
    }

    [Fact]
    public async Task AmbiguousNamesAndMergeCyclesAreRefusedWithoutCreatingRecords()
    {
        var r = await Ready("StoreAmbiguity", links: true); var store = new Neo4jModuleStore(fixture.TransactionRunner, r);
        await Execute("CREATE (:Entity {id:'store-amb-a',name:'same',owner_id:'owner'}), (:Entity {id:'store-amb-b',name:'same',owner_id:'owner'})");
        (await store.WriteAsync(WithLink(Batch(r), "STORE_AMBIGUITY_ABOUT", "same", LinkTargetKind.EntityName))).Single().Status.Should().Be(ProposalWriteStatus.Refused);
        await Execute("MATCH (a:Entity {id:'store-amb-a'}),(b:Entity {id:'store-amb-b'}) SET a.merged_into=b.id,b.merged_into=a.id");
        (await store.WriteAsync(WithLink(Batch(r), "STORE_AMBIGUITY_ABOUT", "store-amb-a"))).Single().Status.Should().Be(ProposalWriteStatus.Refused);
        (await Rows("MATCH (n:StoreAmbiguityItem) RETURN n")).Should().BeEmpty();
    }

    [Fact]
    public async Task SameLogicalKeyForTwoOwnersProducesIndependentRecords()
    {
        var r = await Ready("StoreOwners"); var store = new Neo4jModuleStore(fixture.TransactionRunner, r);
        await Execute("CREATE (:Conversation {id:'StoreOwners-other-c',session_id:'StoreOwners-other-session',user_id:'other'})-[:HAS_MESSAGE]->(:Message {id:'StoreOwners-other-m',conversation_id:'StoreOwners-other-c',session_id:'StoreOwners-other-session',role:'user',content:'lesson'})");
        var batch = Batch(r); var first = (await store.WriteAsync(batch)).Single();
        var other = batch with { Request = batch.Request with { Owner = "other", SessionId = "StoreOwners-other-session", Messages = [new() { Id = "StoreOwners-other-m", Role = "user", Text = "lesson" }] },
            Proposals = [batch.Proposals[0] with { Proposal = batch.Proposals[0].Proposal with { Source = new() { MessageIds = ["StoreOwners-other-m"], Excerpt = "lesson" } } }] };
        var second = (await store.WriteAsync(other)).Single();
        first.Status.Should().Be(ProposalWriteStatus.Stored); second.Status.Should().Be(ProposalWriteStatus.Stored);
        second.RecordId.Should().NotBe(first.RecordId);
        (await Rows("MATCH (n:StoreOwnersItem) WHERE n.invalidated_at IS NULL RETURN n.owner_id AS owner ORDER BY owner"))
            .Select(row => (string)row["owner"]).Should().Equal("other", "owner");
        (await Rows("MATCH (k:ModuleKey {module_id:'com.example.storeowners'}) WHERE k.lock_token IS NOT NULL RETURN k")).Should().BeEmpty();
    }

    [Fact]
    public async Task AssistantSourcesAreAcceptedOnlyWhenTheirRoleWasActuallyPersisted()
    {
        var r = await Ready("StoreAssistant"); var store = new Neo4jModuleStore(fixture.TransactionRunner, r);
        var batch = Batch(r); batch = batch with { Request = batch.Request with { Messages = [batch.Request.Messages[0] with { Role = "assistant" }] } };
        (await store.WriteAsync(batch)).Single().Status.Should().Be(ProposalWriteStatus.Refused);
        await Execute("MATCH (m:Message {id:'StoreAssistant-m1'}) SET m.role='assistant'");
        (await store.WriteAsync(batch)).Single().Status.Should().Be(ProposalWriteStatus.Stored);
    }

    private async Task<ModuleResolution> Ready(string prefix, bool links = false, bool shared = false)
    {
        var resolution = Resolve(prefix, links, shared); await Seed(prefix, "owner");
        await new ModuleMigrator(fixture.TransactionRunner, resolution).ApplyAsync("integration"); return resolution;
    }
    private static ModuleResolution Resolve(string prefix, bool links = false, bool shared = false) => ModuleResolver.Resolve([new()
    {
        Format = "agentmemory.module/v1alpha1", Id = "com.example." + prefix.ToLowerInvariant(), Version = ModuleVersion.Parse("1.0.0"), Sdk = ModuleVersionRange.Parse("[0.1,0.2)"),
        Ontology = new() { LabelPrefix = prefix, Types = [new() { Name = prefix + "Item", Scope = shared ? "shared" : "owner", Properties = new Dictionary<string, ModuleProperty> { ["text"] = new() { Type = "string", Required = true } } }],
            Relations = links ? [new() { Name = System.Text.RegularExpressions.Regex.Replace(prefix, "([a-z0-9])([A-Z])", "$1_$2").ToUpperInvariant() + "_ABOUT", From = prefix + "Item", To = "core:Entity", Required = true, Max = 1 }] : [] }
    }], ModuleVersion.Parse("0.1.0"), new HashSet<string> { "ModuleSchema" });
    private static ModuleWriteBatch Batch(ModuleResolution r, string value = "lesson")
    {
        var module = r.Modules.Single(); var prefix = module.Ontology!.LabelPrefix;
        return new() { ModuleId = module.Id, ManifestFingerprint = r.ManifestFingerprint!, Provider = new() { Id = module.Id + ":writer" },
            Request = new() { SessionId = prefix + "-session", Owner = "owner", Messages = [new() { Id = prefix + "-m1", Role = "user", Text = "lesson" }] },
            AtUtc = DateTimeOffset.Parse("2026-10-14T12:00:00Z"), Proposals = [new() { Index = 0, Proposal = new() { Type = prefix + "Item", Key = "lesson", Properties = new Dictionary<string, JsonElement> { ["text"] = JsonSerializer.SerializeToElement(value) }, Source = new() { MessageIds = [prefix + "-m1"], Excerpt = "lesson" } } }] };
    }
    private static ModuleWriteBatch WithLink(ModuleWriteBatch batch, string relation, string target, LinkTargetKind kind = LinkTargetKind.Identity) => batch with
    { Proposals = [batch.Proposals[0] with { Proposal = batch.Proposals[0].Proposal with { Links = [new() { Relation = relation, Target = target, TargetKind = kind }] } }] };
    private Task Seed(string prefix, string owner) => fixture.TransactionRunner.WriteAsync(async r =>
        { await (await r.RunAsync("CREATE (c:Conversation {id:$c,session_id:$s,user_id:$owner})-[:HAS_MESSAGE]->(:Message {id:$m,conversation_id:$c,session_id:$s,role:'user',content:'lesson'})", new { c = prefix + "-c", s = prefix + "-session", m = prefix + "-m1", owner })).ConsumeAsync(); });
    private Task<List<IRecord>> Rows(string query) => fixture.TransactionRunner.ReadAsync(async r => await (await r.RunAsync(query)).ToListAsync());
    private Task Execute(string query) => fixture.TransactionRunner.WriteAsync(async r => { await (await r.RunAsync(query)).ConsumeAsync(); });
    private sealed class FailingCommit(INeo4jTransactionRunner inner) : INeo4jTransactionRunner
    {
        public Task<T> ReadAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default) => inner.ReadAsync(work, cancellationToken);
        public Task ReadAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default) => inner.ReadAsync(work, cancellationToken);
        public Task<T> WriteAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default) => inner.WriteAsync<T>(async r => { await work(r); throw new IOException("injected before transaction commit"); }, cancellationToken);
        public Task WriteAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default) => inner.WriteAsync(async r => { await work(r); throw new IOException("injected before transaction commit"); }, cancellationToken);
    }
}
