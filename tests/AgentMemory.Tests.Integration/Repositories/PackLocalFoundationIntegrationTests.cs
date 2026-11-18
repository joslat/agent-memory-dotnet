#pragma warning disable AMEXT001
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Bundles;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Extensibility.Reading;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Neo4j.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;
using NSubstitute;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.Repositories;

public sealed partial class ModuleRecordWriteIntegrationTests
{
    static KnowledgePack FoundationPack(string version = "1.0.0", KnowledgePack? previous = null, int nameLimit = 100, bool removeFirst = false)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var id = previous?.Id ?? "io.example.foundation" + suffix;
        var prefix = previous?.Manifest.Ontology!.LabelPrefix ?? "Foundation" + suffix;
        var manifest = PackManifest.Replace("VERSION", version, StringComparison.Ordinal)
            .Replace("io.example.packdojo", id, StringComparison.Ordinal).Replace("PackDojo", prefix, StringComparison.Ordinal)
            .Replace("PACK_DOJO", prefix.ToUpperInvariant(), StringComparison.Ordinal)
            .Replace("\"maxLength\":100", "\"maxLength\":" + nameLimit, StringComparison.Ordinal);
        var declaration = JsonNode.Parse(manifest)!.AsObject();
        declaration["traversal"] = new JsonObject
        {
            ["anchors"] = new JsonArray(new JsonObject { ["type"] = prefix + "Kata", ["property"] = "name", ["match"] = "exact", ["kind"] = "symbol" }),
            ["relations"] = new JsonArray(new JsonObject { ["name"] = prefix.ToUpperInvariant() + "_KATA_FOR_RANK", ["direction"] = "out", ["fanOut"] = 2, ["weightMicro"] = 800000 }),
            ["maxItems"] = 4
        };
        var content = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [KnowledgePack.ManifestFile] = Encoding.UTF8.GetBytes(declaration.ToJsonString()),
            ["seed/records.jsonl"] = Encoding.UTF8.GetBytes((string.Join("\n", removeFirst ? Version1.Skip(1) : Version1) + "\n")
                .Replace("PackDojo", prefix, StringComparison.Ordinal).Replace("PACK_DOJO", prefix.ToUpperInvariant(), StringComparison.Ordinal))
        };
        var trust = new BundleTrust(); trust.DeveloperKeys.Add(PackKey.PublicKeyPem);
        return KnowledgePack.Open(BundleSigning.Sign(content, id, version, PackKey.PrivateKeyPem), trust);
    }

    async Task LocalSchema(Neo4jIntegrationFixture? target = null)
    {
        target ??= fixture;
        var services = new ServiceCollection(); services.AddLogging();
        services.AddNeo4jAgentMemory(options =>
        {
            options.Uri = target.ConnectionString; options.Username = target.User; options.Password = target.Password;
            options.EmbeddingDimensions = 4; options.Extensions.Add("pack-local-state");
        });
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync();
    }

    [Fact]
    public async Task Local_foundation_the_same_identity_in_two_physical_stores_has_independent_local_state()
    {
        await LocalSchema(); var other = new Neo4jIntegrationFixture();
        await other.InitializeAsync();
        try
        {
            await LocalSchema(other); var pack = FoundationPack(); using var services = Policies();
            var isolation = services.GetRequiredService<IMemoryIsolationPolicy>(); var identity = LocalIdentity(pack);
            var first = new Neo4jPackLocalStore(fixture.TransactionRunner, isolation, [pack]);
            var second = new Neo4jPackLocalStore(other.TransactionRunner, isolation, [pack]);
            var policy = await first.MountAsync(identity, writeProtected: false);
            await second.MountAsync(identity);
            await first.WriteAsync(identity, policy.Revision, LocalName(pack, "Only in the first store"));
            Assert.Single((await first.ReadAsync(identity)).Changes);
            var untouched = await second.ReadAsync(identity);
            Assert.Empty(untouched.Changes); Assert.True(untouched.Policy.WriteProtected);
        }
        finally { await other.DisposeAsync(); }
    }

    static PackLocalIdentity LocalIdentity(KnowledgePack pack, string application = "foundation", string owner = "alice") =>
        new() { ApplicationId = application, OwnerId = owner, PackId = pack.Id, BaseDigest = pack.Digest };

    static ModuleProposal LocalName(KnowledgePack pack, string name) => pack.Proposals.First(p => p.Type.EndsWith("Kata", StringComparison.Ordinal)) with
    {
        Properties = pack.Proposals.First(p => p.Type.EndsWith("Kata", StringComparison.Ordinal)).Properties
            .ToDictionary(p => p.Key, p => p.Key == "name" ? JsonSerializer.SerializeToElement(name) : p.Value),
        Source = new() { Excerpt = "User edited this local syllabus" }, SourceKey = null, ConfidenceMicro = null
    };

    [Fact]
    public async Task Local_foundation_writes_honor_the_resolved_write_owner_before_any_database_work()
    {
        var pack = FoundationPack(); var identity = LocalIdentity(pack);
        var policy = Substitute.For<IMemoryIsolationPolicy>();
        policy.ResolveReadScope(Arg.Any<MemoryScope?>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<MemoryOperationAccess>()).Returns(MemoryScope.For("alice"));
        policy.ResolveWriteOwner(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<MemoryOperationAccess>()).Returns("bob");
        var runner = Substitute.For<INeo4jTransactionRunner>();
        var store = new Neo4jPackLocalStore(runner, policy, [pack]);
        var proposal = LocalName(pack, "Owner-bound edit");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.MountAsync(identity));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ConfigureAsync(identity, 0, false, false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(identity, 0, proposal));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RestoreAsync(identity, 0, proposal.Type, proposal.Key));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DetachAsync(identity, 0));
        policy.ResolveReadScope(Arg.Any<MemoryScope?>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<MemoryOperationAccess>()).Returns(MemoryScope.For("bob"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PeekAsync(identity));
        Assert.Empty(runner.ReceivedCalls());
    }

    [Fact]
    public async Task Local_foundation_two_owners_and_two_brains_keep_one_published_baseline_and_reconnect_independently()
    {
        await LocalSchema(); var pack = FoundationPack(); using var services = Policies();
        var isolation = services.GetRequiredService<IMemoryIsolationPolicy>();
        var store = new Neo4jPackLocalStore(fixture.TransactionRunner, isolation, [pack]);
        var importer = new KnowledgePackImporter(new Neo4jKnowledgePackStore(fixture.TransactionRunner));
        await importer.ImportAsync(pack);
        var alice = LocalIdentity(pack); var bob = alice with { OwnerId = "bob" }; var otherBrain = alice with { ApplicationId = "another-brain" };
        foreach (var identity in new[] { alice, bob, otherBrain }) await store.MountAsync(identity);
        var policy = await store.ConfigureAsync(alice, 0, false, false);
        var local = LocalName(pack, "Starlight harmony");
        local = local with { Links = [local.Links.Single() with { Target = "7-kyu" }] };
        await store.WriteAsync(alice, policy.Revision, local);
        var reconnected = new Neo4jPackLocalStore(fixture.TransactionRunner, isolation, [pack]);
        var state = await reconnected.ReadAsync(alice);
        Assert.False(state.Policy.WriteProtected);
        Assert.Empty((await reconnected.ReadAsync(bob)).Changes);
        Assert.Empty((await reconnected.ReadAsync(otherBrain)).Changes);
        var memory = PackMemory.From([pack]).WithLocalChanges(new Dictionary<string, IReadOnlyList<ModuleProposal>> { [pack.Id] = state.Changes.Select(c => c.Proposal!).ToArray() });
        var query = new ModuleQuery { ModuleId = pack.Id, Type = local.Type, Key = local.Key, Limit = 1 };
        var context = new ModuleReadContext { Scope = MemoryScope.For("alice") };
        Assert.Equal("Starlight harmony", Assert.Single((await memory.ReaderFor(pack.Id).FindAsync(query, context)).Records).Properties["name"].GetString());
        var search = query with { Key = null, Search = new() { ViewId = "kata", Terms = ["starlight"] } };
        Assert.Single((await memory.ReaderFor(pack.Id).FindAsync(search, context)).Records);
        var effective = Assert.Single((await memory.ReaderFor(pack.Id).FindAsync(query, context)).Records);
        var meaning = memory.ReaderFor(pack.Id, new Dictionary<string, double> { [effective.Id] = 0.95 });
        Assert.Single((await meaning.FindAsync(search with { Search = search.Search! with { Terms = ["paraphrase"] } }, context)).Records);
        Assert.Empty((await memory.ReaderFor(pack.Id).FindAsync(query with { Where = [Filter("name", "=", "Heian Shodan")] }, context)).Records);
        var matched = await memory.MatchAsync(new() { ModuleId = pack.Id, Type = local.Type, Property = "name", Match = ModuleAnchorMatch.Exact, Values = ["Starlight harmony"] }, context);
        Assert.Single(matched.Records);
        var reached = await memory.ExpandAsync(new() { ModuleId = pack.Id, Relation = local.Links.Single().Relation, Direction = ModuleTraversalDirection.Out, FromIds = [effective.Id] }, context);
        Assert.Equal("7-kyu", Assert.Single(reached.Edges).To.Key);
        var published = new Neo4jModuleReader(fixture.TransactionRunner, pack.Resolution, pack.Id, isolation);
        var baseRecord = Assert.Single((await published.FindAsync(query, context)).Records);
        Assert.NotEqual("Starlight harmony", baseRecord.Properties["name"].GetString());
        Assert.Empty((await published.FindAsync(search, context)).Records);
        var unchanged = await importer.ImportAsync(pack);
        Assert.Equal(pack.Proposals.Count, unchanged.Unchanged);
    }

    [Fact]
    public async Task Local_foundation_a_real_queued_writer_cannot_cross_a_committed_protection_lock()
    {
        await LocalSchema(); var pack = FoundationPack(); using var services = Policies();
        var isolation = services.GetRequiredService<IMemoryIsolationPolicy>(); var identity = LocalIdentity(pack);
        var store = new Neo4jPackLocalStore(fixture.TransactionRunner, isolation, [pack]);
        var policy = await store.MountAsync(identity, writeProtected: false);
        var pause = new PauseLocalCommit(fixture.TransactionRunner);
        var protecting = new Neo4jPackLocalStore(pause, isolation, [pack]).ConfigureAsync(identity, policy.Revision, true, false);
        await pause.HoldingLock.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new Neo4jPackLocalStore(new SignalLocalStart(fixture.TransactionRunner, started), isolation, [pack]);
        var writing = writer.WriteAsync(identity, policy.Revision, LocalName(pack, "Queued local assertion"));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var observedBlockedTransaction = false;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var rows = await Rows("SHOW TRANSACTIONS YIELD currentQuery,status WHERE currentQuery CONTAINS 'PackLocalMount' AND status STARTS WITH 'Blocked' RETURN count(*) AS count");
                if ((long)rows.Single()["count"] > 0) { observedBlockedTransaction = true; break; }
                if (writing.IsCompleted) break;
                await Task.Delay(25);
            }
            Assert.True(observedBlockedTransaction, "Neo4j must report the queued transaction blocked on the held mount lock.");
            Assert.False(writing.IsCompleted, "The writer must wait behind the held policy lock.");
        }
        finally { pause.Release.TrySetResult(); }
        Assert.True((await protecting).WriteProtected);
        await Assert.ThrowsAsync<InvalidOperationException>(() => writing);
        Assert.Empty((await store.ReadAsync(identity)).Changes);
    }

    [Fact]
    public async Task Local_foundation_compatible_upgrade_retains_amendments_policy_and_history_but_refuses_old_revision_reads()
    {
        await LocalSchema(); var v1 = FoundationPack(); var v2 = FoundationPack("1.1.0", v1); using var services = Policies();
        var isolation = services.GetRequiredService<IMemoryIsolationPolicy>(); var oldIdentity = LocalIdentity(v1);
        var oldStore = new Neo4jPackLocalStore(fixture.TransactionRunner, isolation, [v1]);
        var policy = await oldStore.MountAsync(oldIdentity, writeProtected: false);
        var local = LocalName(v1, "A compatible personal syllabus");
        await oldStore.WriteAsync(oldIdentity, policy.Revision, local);
        policy = (await oldStore.ReadAsync(oldIdentity)).Policy;
        policy = await oldStore.ConfigureAsync(oldIdentity, policy.Revision, true, true);
        var newStore = new Neo4jPackLocalStore(fixture.TransactionRunner, isolation, [v1, v2]); var next = oldIdentity with { BaseDigest = v2.Digest };
        var upgraded = await newStore.MountAsync(next, writeProtected: false);
        Assert.True(upgraded.WriteProtected); Assert.True(upgraded.AutomaticLearning);
        Assert.Equal(v2.Digest, upgraded.BaseDigest);
        Assert.Equal(local.Properties["name"].GetString(), Assert.Single((await newStore.ReadAsync(next)).Changes).Proposal!.Properties["name"].GetString());
        Assert.Single(await newStore.HistoryAsync(next, local.Type, local.Key));
        await Assert.ThrowsAsync<InvalidOperationException>(() => oldStore.ReadAsync(oldIdentity));
        await Assert.ThrowsAsync<InvalidOperationException>(() => oldStore.WriteAsync(oldIdentity, policy.Revision, local));
    }

    [Fact]
    public async Task Local_foundation_incompatible_upgrade_keeps_the_old_mount_and_every_personal_value()
    {
        await LocalSchema(); var v1 = FoundationPack(); var v2 = FoundationPack("1.1.0", v1, nameLimit: 15); using var services = Policies();
        var isolation = services.GetRequiredService<IMemoryIsolationPolicy>(); var identity = LocalIdentity(v1);
        var store = new Neo4jPackLocalStore(fixture.TransactionRunner, isolation, [v1]);
        var policy = await store.MountAsync(identity, writeProtected: false);
        var local = LocalName(v1, "A long personal syllabus exceeding the new schema bound");
        await store.WriteAsync(identity, policy.Revision, local);
        var upgrade = new Neo4jPackLocalStore(fixture.TransactionRunner, isolation, [v1, v2]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => upgrade.MountAsync(identity with { BaseDigest = v2.Digest }));
        var retained = await store.ReadAsync(identity);
        Assert.Equal(v1.Digest, retained.Policy.BaseDigest);
        Assert.Equal(local.Properties["name"].GetString(), Assert.Single(retained.Changes).Proposal!.Properties["name"].GetString());
        Assert.Single(await store.HistoryAsync(identity, local.Type, local.Key));
    }

    sealed class PauseLocalCommit(INeo4jTransactionRunner inner) : INeo4jTransactionRunner
    {
        public TaskCompletionSource HoldingLock { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<T> ReadAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default) => inner.ReadAsync(work, cancellationToken);
        public Task ReadAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default) => inner.ReadAsync(work, cancellationToken);
        public Task<T> WriteAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default) => inner.WriteAsync(async tx =>
        { var result = await work(tx); HoldingLock.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30)); return result; }, cancellationToken);
        public Task WriteAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    sealed class SignalLocalStart(INeo4jTransactionRunner inner, TaskCompletionSource started) : INeo4jTransactionRunner
    {
        public Task<T> ReadAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default) => inner.ReadAsync(work, cancellationToken);
        public Task ReadAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default) => inner.ReadAsync(work, cancellationToken);
        public Task<T> WriteAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default) => inner.WriteAsync(tx => { started.TrySetResult(); return work(tx); }, cancellationToken);
        public Task WriteAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default) => inner.WriteAsync(tx => { started.TrySetResult(); return work(tx); }, cancellationToken);
    }
}
