#pragma warning disable AMEXT001
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Packs;
using Microsoft.Extensions.DependencyInjection;

namespace AgentMemory.Tests.Integration.Repositories;

public sealed partial class ModuleRecordWriteIntegrationTests
{
    [Fact]
    public async Task Local_erasure_serializes_a_queued_writer_before_admitting_any_new_payload()
    {
        await LocalSchema(); var pack = FoundationPack(); using var services = Policies();
        var isolation = services.GetRequiredService<IMemoryIsolationPolicy>();
        var local = new Neo4jPackLocalStore(fixture.TransactionRunner, isolation, [pack]); var identity = LocalIdentity(pack);
        var policy = await local.MountAsync(identity, false); var proposal = LocalName(pack, "Queued withdrawn value");
        await new KnowledgePackImporter(new Neo4jKnowledgePackStore(fixture.TransactionRunner)).ImportAsync(pack);
        var erasure = new ModuleErasure { PackId = pack.Id, Statement = "sha256:" + new string('d', 64), IssuedAt = DateTimeOffset.UtcNow,
            Reason = "withdrawn", Salt = "0123456789abcdef0123456789abcdef", Records = [new(proposal.Type, proposal.Key, null)] };
        var pause = new PauseLocalCommit(fixture.TransactionRunner);
        var erasing = new Neo4jModuleStore(new PackLocalErasureRunner(pause, pack.Manifest, [erasure]), pack.Resolution)
            .ErasePackAsync(pack.Id, [erasure], DateTimeOffset.UtcNow, CancellationToken.None);
        await pause.HoldingLock.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var writing = local.WriteAsync(identity, policy.Revision, proposal);
        try
        {
            var blocked = false; var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var rows = await Rows("SHOW TRANSACTIONS YIELD currentQuery,status WHERE currentQuery CONTAINS 'PackLocalGate' AND status STARTS WITH 'Blocked' RETURN count(*) AS count");
                if ((long)rows.Single()["count"] > 0) { blocked = true; break; }
                if (writing.IsCompleted) break;
                await Task.Delay(25);
            }
            Assert.True(blocked, "The writer must block on the pack's canonical withdrawal lock.");
        }
        finally { pause.Release.TrySetResult(); }
        await erasing; await Assert.ThrowsAsync<InvalidOperationException>(() => writing);
        Assert.Empty((await local.ReadAsync(identity)).Changes);
    }

    [Fact]
    public async Task Local_erasure_tombstones_suppress_current_past_and_history_copies_and_refuse_new_writers()
    {
        await LocalSchema(); var pack = FoundationPack(); using var services = Policies();
        var local = new Neo4jPackLocalStore(fixture.TransactionRunner, services.GetRequiredService<IMemoryIsolationPolicy>(), [pack]);
        var identity = LocalIdentity(pack); var policy = await local.MountAsync(identity, writeProtected: false);
        var proposal = LocalName(pack, "Personal value for a record later withdrawn");
        await local.WriteAsync(identity, policy.Revision, proposal); var beforeErase = DateTimeOffset.UtcNow;
        var erasure = new ModuleErasure { PackId = pack.Id, Statement = "sha256:" + new string('e', 64), IssuedAt = DateTimeOffset.UtcNow,
            Reason = "withdrawn", Salt = "0123456789abcdef0123456789abcdef", Records = [new(proposal.Type, proposal.Key, null)] };
        await new Neo4jKnowledgePackStore(fixture.TransactionRunner, maintainLocalCopies: true).EraseAsync(pack.Manifest, [erasure], DateTimeOffset.UtcNow);
        var state = await local.ReadAsync(identity);
        Assert.Empty(state.Changes); Assert.Single(state.Erasures);
        Assert.Empty((await local.ReadAsync(identity, beforeErase)).Changes);
        Assert.Empty(await local.HistoryAsync(identity, proposal.Type, proposal.Key));
        Assert.Empty(await Rows("MATCH (p:PackLocalMount {pack_id:'" + pack.Id + "'}), (c:PackLocalChange {mount_id:p.id}) RETURN c"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => local.WriteAsync(identity, state.Policy.Revision, proposal));
        await Assert.ThrowsAsync<InvalidOperationException>(() => local.RestoreAsync(identity, state.Policy.Revision, proposal.Type, proposal.Key));
    }
}
