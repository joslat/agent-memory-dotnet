#pragma warning disable AMEXT001
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Neo4j.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// PACK-RELOAD-RCA, the local-copy path (suspect b of 19 Nov, never run before): a host with local pack copies removes a
/// pack by detaching its mount (the shared records and the owner's history stay) and loads it again by mounting the same
/// pack, same digest, on the next connect. The default path (import, remove, import again) is covered by
/// PackReloadIntegrationTests; the fault seen at the 19 Nov talk was a new pack id refused the old id's names, fixed by
/// KnowledgePackImporter.TransferAsync.
/// </summary>
public sealed partial class ModuleRecordWriteIntegrationTests
{
    /// <summary>
    /// The reload works: the mount is live again on its base, in a new store object as a new session would use, and the
    /// owner's values are all there. Its protection follows the host lifecycle (R-HOST-069, 24 Nov 2026): the detach protected
    /// it only while it was out, so a copy the owner kept writable takes the host's request when it is loaded again.
    /// </summary>
    [Fact]
    public async Task Local_copy_the_owner_kept_writable_is_live_again_with_its_values_and_takes_the_host_s_request()
    {
        await LocalSchema(); var pack = FoundationPack(); using var services = Policies();
        var isolation = services.GetRequiredService<IMemoryIsolationPolicy>();
        var store = new Neo4jPackLocalStore(fixture.TransactionRunner, isolation, [pack]); var identity = LocalIdentity(pack);
        var policy = await store.MountAsync(identity, writeProtected: false);
        await store.WriteAsync(identity, policy.Revision, LocalName(pack, "Kept across a reload"));

        // The removal: the mount is detached, and protected while it is out.
        var detached = await store.DetachAsync(identity, (await store.PeekAsync(identity))!.Revision);
        Assert.False(detached.Active);
        Assert.True(detached.WriteProtected);

        // The next connect loads the same pack again, asking for a writable copy: the owner kept it writable, so it is.
        var reconnected = new Neo4jPackLocalStore(fixture.TransactionRunner, isolation, [pack]);
        var again = await reconnected.MountAsync(identity, writeProtected: false);
        Assert.True(again.Active);
        Assert.Equal(pack.Digest, again.BaseDigest);
        Assert.False(again.WriteProtected, "the detach protected it only while it was out");
        Assert.Equal("Kept across a reload",
            Assert.Single((await reconnected.ReadAsync(identity)).Changes).Proposal!.Properties["name"].GetString());
        await reconnected.WriteAsync(identity, again.Revision, LocalName(pack, "Written after the reload"));
        Assert.Equal("Written after the reload",
            Assert.Single((await reconnected.ReadAsync(identity)).Changes).Proposal!.Properties["name"].GetString());
    }

    /// <summary>A copy the owner protected before the removal stays protected when it is loaded again, whatever the host asks.</summary>
    [Fact]
    public async Task Local_copy_the_owner_protected_stays_protected_when_loaded_again_whatever_the_host_asks()
    {
        await LocalSchema(); var pack = FoundationPack(); using var services = Policies();
        var isolation = services.GetRequiredService<IMemoryIsolationPolicy>();
        var store = new Neo4jPackLocalStore(fixture.TransactionRunner, isolation, [pack]); var identity = LocalIdentity(pack, owner: "owner-protects");
        var policy = await store.MountAsync(identity, writeProtected: false);
        await store.WriteAsync(identity, policy.Revision, LocalName(pack, "Before the owner protected it"));
        var protectedByOwner = await store.ConfigureAsync(identity, (await store.PeekAsync(identity))!.Revision, writeProtected: true, automaticLearning: false);
        Assert.True(protectedByOwner.WriteProtected);
        await store.DetachAsync(identity, protectedByOwner.Revision);

        var again = await new Neo4jPackLocalStore(fixture.TransactionRunner, isolation, [pack]).MountAsync(identity, writeProtected: false);

        Assert.True(again.Active);
        Assert.True(again.WriteProtected, "the owner's own protection outlives the removal");
    }
}
