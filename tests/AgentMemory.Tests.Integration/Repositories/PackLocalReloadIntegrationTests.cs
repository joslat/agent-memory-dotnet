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
    /// owner's values are all there. As the contract says (a mount "returns its existing captured policy", a detach
    /// "restrictively deactivates"), it comes back write-protected whatever the host asks, until the owner configures it.
    /// </summary>
    [Fact]
    public async Task Local_copy_detached_and_mounted_again_is_live_keeps_its_values_and_stays_protected_until_configured()
    {
        await LocalSchema(); var pack = FoundationPack(); using var services = Policies();
        var isolation = services.GetRequiredService<IMemoryIsolationPolicy>();
        var store = new Neo4jPackLocalStore(fixture.TransactionRunner, isolation, [pack]); var identity = LocalIdentity(pack);
        var policy = await store.MountAsync(identity, writeProtected: false);
        await store.WriteAsync(identity, policy.Revision, LocalName(pack, "Kept across a reload"));

        // The removal: the mount is detached, nothing else.
        var detached = await store.DetachAsync(identity, (await store.PeekAsync(identity))!.Revision);
        Assert.False(detached.Active);

        // The next connect loads the same pack again, asking for a writable copy.
        var reconnected = new Neo4jPackLocalStore(fixture.TransactionRunner, isolation, [pack]);
        var again = await reconnected.MountAsync(identity, writeProtected: false);
        Assert.True(again.Active);
        Assert.Equal(pack.Digest, again.BaseDigest);
        Assert.True(again.WriteProtected, "a detach is restrictive, and a mount keeps the captured policy");
        Assert.Equal("Kept across a reload",
            Assert.Single((await reconnected.ReadAsync(identity)).Changes).Proposal!.Properties["name"].GetString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => reconnected.WriteAsync(identity, again.Revision, LocalName(pack, "Refused")));

        // The owner makes it writable again; then it takes writes.
        var writable = await reconnected.ConfigureAsync(identity, again.Revision, writeProtected: false, automaticLearning: false);
        await reconnected.WriteAsync(identity, writable.Revision, LocalName(pack, "Written after the reload"));
        Assert.Equal("Written after the reload",
            Assert.Single((await reconnected.ReadAsync(identity)).Changes).Proposal!.Properties["name"].GetString());
    }
}
