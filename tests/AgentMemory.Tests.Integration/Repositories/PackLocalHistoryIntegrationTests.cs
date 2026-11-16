#pragma warning disable AMEXT001
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Neo4j.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace AgentMemory.Tests.Integration.Repositories;

public sealed partial class ModuleRecordWriteIntegrationTests
{
    [Fact]
    public async Task Local_upgrade_cannot_downgrade_another_host()
    {
        await LocalSchema(); var first = FoundationPack(); var next = FoundationPack("1.1.0", first); using var services = Policies();
        var isolation = services.GetRequiredService<IMemoryIsolationPolicy>();
        var store = new Neo4jPackLocalStore(fixture.TransactionRunner, isolation, [first, next]);
        await store.MountAsync(LocalIdentity(next), false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.MountAsync(LocalIdentity(first)));
        Assert.Equal(next.Digest, (await store.ReadAsync(LocalIdentity(next))).Policy.BaseDigest);
    }

    [Fact]
    public async Task Local_upgrade_keeps_the_old_mount_when_a_published_key_with_an_override_disappears()
    {
        await LocalSchema(); var first = FoundationPack(); var next = FoundationPack("1.1.0", first, removeFirst: true); using var services = Policies();
        var store = new Neo4jPackLocalStore(fixture.TransactionRunner, services.GetRequiredService<IMemoryIsolationPolicy>(), [first, next]);
        var identity = LocalIdentity(first); var policy = await store.MountAsync(identity, false);
        await store.WriteAsync(identity, policy.Revision, LocalName(first, "Personal value must remain an override"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.MountAsync(LocalIdentity(next)));
        Assert.Equal(first.Digest, (await store.ReadAsync(identity)).Policy.BaseDigest);
    }

    [Fact]
    public async Task Local_history_pins_the_published_digest_and_policy_that_were_effective_at_the_requested_clock()
    {
        await LocalSchema(); var first = FoundationPack(); var next = FoundationPack("1.1.0", first); using var services = Policies();
        var isolation = services.GetRequiredService<IMemoryIsolationPolicy>(); var clock = new LocalHistoryClock();
        var identity = LocalIdentity(first); var store = new Neo4jPackLocalStore(fixture.TransactionRunner, isolation, [first], clock);
        var policy = await store.MountAsync(identity, writeProtected: false);
        clock.Advance(); await store.WriteAsync(identity, policy.Revision, LocalName(first, "Personal value at the old base"));
        var past = clock.GetUtcNow(); policy = (await store.ReadAsync(identity)).Policy;
        clock.Advance(); await store.ConfigureAsync(identity, policy.Revision, true, false);
        clock.Advance(); var upgraded = new Neo4jPackLocalStore(fixture.TransactionRunner, isolation, [first, next], clock); var nextIdentity = LocalIdentity(next);
        await upgraded.MountAsync(nextIdentity);
        var historical = await upgraded.ReadAsync(nextIdentity, past);
        Assert.Equal(first.Digest, historical.Policy.BaseDigest); Assert.False(historical.Policy.WriteProtected); Assert.Equal(1, historical.Policy.Revision);
        Assert.Equal("Personal value at the old base", Assert.Single(historical.Changes).Proposal!.Properties["name"].GetString());
        Assert.Equal(next.Digest, (await upgraded.ReadAsync(nextIdentity)).Policy.BaseDigest);
    }

    [Fact]
    public async Task Local_history_preserves_each_change_restore_marker_and_historical_concurrency_revision()
    {
        await LocalSchema(); var pack = FoundationPack(); using var services = Policies(); var clock = new LocalHistoryClock();
        var store = new Neo4jPackLocalStore(fixture.TransactionRunner, services.GetRequiredService<IMemoryIsolationPolicy>(), [pack], clock); var identity = LocalIdentity(pack);
        var policy = await store.MountAsync(identity, writeProtected: false); clock.Advance();
        var first = LocalName(pack, "First personal value"); await store.WriteAsync(identity, policy.Revision, first); var atFirst = clock.GetUtcNow();
        policy = (await store.ReadAsync(identity)).Policy; clock.Advance();
        await store.WriteAsync(identity, policy.Revision, LocalName(pack, "Second personal value")); var atSecond = clock.GetUtcNow();
        policy = (await store.ReadAsync(identity)).Policy; clock.Advance();
        await store.RestoreAsync(identity, policy.Revision, first.Type, first.Key); var atRestore = clock.GetUtcNow();
        var a = await store.ReadAsync(identity, atFirst); var b = await store.ReadAsync(identity, atSecond); var restored = await store.ReadAsync(identity, atRestore);
        Assert.Equal(1, a.Policy.Revision); Assert.Equal(2, b.Policy.Revision); Assert.Equal(3, restored.Policy.Revision);
        Assert.Equal("First personal value", Assert.Single(a.Changes).Proposal!.Properties["name"].GetString());
        Assert.Equal("Second personal value", Assert.Single(b.Changes).Proposal!.Properties["name"].GetString());
        Assert.Null(Assert.Single(restored.Changes).Proposal);
        Assert.Equal(3, (await store.HistoryAsync(identity, first.Type, first.Key)).Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAsync(identity, atFirst.AddDays(-1)));
    }

    sealed class LocalHistoryClock : TimeProvider
    {
        DateTimeOffset _at = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _at;
        public void Advance() => _at = _at.AddSeconds(1);
    }
}
