#pragma warning disable AMEXT001
using System.Text.Json;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Extensibility.Neo4j.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentMemory.Tests.Integration.Repositories;

public sealed partial class ModuleRecordWriteIntegrationTests
{
    [Fact]
    public async Task Local_status_batch_exposes_only_scoped_policy_and_current_assertion_presence()
    {
        await LocalSchema(); var one = FoundationPack(); var two = FoundationPack(); using var services = Policies();
        var isolation = services.GetRequiredService<IMemoryIsolationPolicy>();
        var store = new Neo4jPackLocalStore(fixture.TransactionRunner, isolation, [one, two]); var first = LocalIdentity(one); var second = LocalIdentity(two);
        var policy = await store.MountAsync(first, false); await store.MountAsync(second);
        var assertion = LocalName(one, "A private value never included in routing metadata"); await store.WriteAsync(first, policy.Revision, assertion);
        IPackLocalStore metadata = new Neo4jPackLocalStore(fixture.TransactionRunner, isolation, []);
        var statuses = await metadata.PeekManyAsync([first, second]);
        Assert.Equal(2, statuses.Count); Assert.True(statuses[one.Id].HasAssertions); Assert.False(statuses[two.Id].HasAssertions);
        Assert.DoesNotContain("A private value", JsonSerializer.Serialize(statuses), StringComparison.Ordinal);
        await store.RestoreAsync(first, statuses[one.Id].Policy.Revision, assertion.Type, assertion.Key);
        Assert.False((await metadata.PeekManyAsync([first]))[one.Id].HasAssertions);
        Assert.Empty(await metadata.PeekManyAsync([first with { ApplicationId = first.ApplicationId + "-other" }]));
        await Assert.ThrowsAsync<ArgumentException>(() => metadata.PeekManyAsync([first, second with { OwnerId = "bob" }]));
    }

    [Fact]
    public async Task Local_detach_uses_only_scoped_metadata_and_preserves_other_owners_and_all_history()
    {
        await LocalSchema(); var pack = FoundationPack(); using var services = Policies();
        var isolation = services.GetRequiredService<IMemoryIsolationPolicy>();
        var store = new Neo4jPackLocalStore(fixture.TransactionRunner, isolation, [pack]); var identity = LocalIdentity(pack);
        var policy = await store.MountAsync(identity, false); await store.WriteAsync(identity, policy.Revision, LocalName(pack, "Retained personal value"));
        var bob = identity with { OwnerId = "bob" }; await store.MountAsync(bob, false);
        var metadata = new Neo4jPackLocalStore(fixture.TransactionRunner, isolation, []);
        var unavailable = identity with { BaseDigest = "unavailable" };
        Assert.Null(await metadata.PeekAsync(unavailable with { ApplicationId = identity.ApplicationId + "-other" }));
        policy = (await metadata.PeekAsync(unavailable))!; Assert.Equal(1, policy.Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => metadata.DetachAsync(unavailable, 0));
        var detached = await metadata.DetachAsync(unavailable, policy.Revision);
        Assert.False(detached.Active); Assert.True(detached.WriteProtected); Assert.False(detached.AutomaticLearning); Assert.Equal(pack.Digest, detached.BaseDigest);
        Assert.True((await metadata.PeekAsync(bob))!.Active);
        var local = LocalName(pack, "New edit");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(identity, detached.Revision, local));
        Assert.Single(await store.HistoryAsync(identity, local.Type, local.Key));
        Assert.Single((await store.ReadAsync(identity)).Changes);
    }

    [Fact]
    public async Task Local_copy_protection_rechecks_queued_writes_and_keeps_owner_history_separate()
    {
        await LocalSchema();
        var pack = DojoPack("1.0.0", Version1);
        using var services = Policies();
        var store = new Neo4jPackLocalStore(fixture.TransactionRunner, services.GetRequiredService<IMemoryIsolationPolicy>(), [pack]);
        var identity = new PackLocalIdentity { ApplicationId = "local-test-" + Guid.NewGuid().ToString("N"), OwnerId = "alice", PackId = pack.Id, BaseDigest = pack.Digest };
        var policy = await store.MountAsync(identity);
        Assert.True(policy.WriteProtected);
        var original = pack.Proposals.First(p => p.Type == "PackDojoKata");
        var properties = original.Properties.ToDictionary(p => p.Key, p => p.Value);
        properties["name"] = JsonSerializer.SerializeToElement("Local syllabus");
        var proposal = original with { Properties = properties, Source = new() { Excerpt = "Alice edited her syllabus" }, SourceKey = null, ConfidenceMicro = null };
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(identity, policy.Revision, proposal));
        policy = await store.ConfigureAsync(identity, policy.Revision, false, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(identity, policy.Revision, proposal, automatic: true));
        await store.WriteAsync(identity, policy.Revision, proposal);
        var snapshot = await store.ReadAsync(identity);
        Assert.Equal("Local syllabus", Assert.Single(snapshot.Changes).Proposal!.Properties["name"].GetString());
        var queuedRevision = snapshot.Policy.Revision;
        policy = await store.ConfigureAsync(identity, queuedRevision, true, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(identity, queuedRevision, proposal));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(identity, policy.Revision, proposal));
        Assert.Single((await store.ReadAsync(identity)).Changes);
        var bob = identity with { OwnerId = "bob" };
        await store.MountAsync(bob);
        Assert.Empty((await store.ReadAsync(bob)).Changes);
        policy = await store.ConfigureAsync(identity, policy.Revision, false, false);
        await store.RestoreAsync(identity, policy.Revision, proposal.Type, proposal.Key);
        Assert.Null(Assert.Single((await store.ReadAsync(identity)).Changes).Proposal);
        Assert.Equal(2, (await store.HistoryAsync(identity, proposal.Type, proposal.Key)).Count);
        Assert.NotEqual("Local syllabus", original.Properties["name"].GetString());
    }
}
