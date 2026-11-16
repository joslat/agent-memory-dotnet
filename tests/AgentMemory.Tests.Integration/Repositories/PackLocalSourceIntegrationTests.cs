#pragma warning disable AMEXT001
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Neo4j.Storage;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Repositories;

public sealed partial class ModuleRecordWriteIntegrationTests
{
    [Fact]
    public async Task Local_automatic_writer_requires_actual_persisted_user_text_and_owner()
    {
        await LocalSchema(); var pack = FoundationPack(); using var services = Policies();
        var store = new Neo4jPackLocalStore(fixture.TransactionRunner, services.GetRequiredService<IMemoryIsolationPolicy>(), [pack]);
        var identity = LocalIdentity(pack); var policy = await store.MountAsync(identity, false);
        policy = await store.ConfigureAsync(identity, policy.Revision, false, true);
        var id = "local-source-" + Guid.NewGuid().ToString("N");
        await fixture.TransactionRunner.WriteAsync(async tx =>
        { await (await tx.RunAsync("CREATE (c:Conversation {id:$id,session_id:$id,user_id:'alice'})-[:HAS_MESSAGE]->(:Message {id:$id,session_id:$id,conversation_id:$id,role:'user',content:'My actual personal syllabus'})", new { id })).ConsumeAsync(); });
        var proposal = LocalName(pack, "Personal syllabus") with { Source = new() { MessageIds = [id], Excerpt = "Invented source text" } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(identity, policy.Revision, proposal, true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(identity, policy.Revision, proposal with { Source = new() { MessageIds = [id + "missing"], Excerpt = "My actual personal syllabus" } }, true));
        await store.WriteAsync(identity, policy.Revision, proposal with { Source = new() { MessageIds = [id], Excerpt = "My actual personal syllabus" } }, true);
        Assert.Equal("system", Assert.Single((await store.ReadAsync(identity)).Changes).Actor);
        var bob = identity with { OwnerId = "bob" }; var other = await store.MountAsync(bob, false);
        other = await store.ConfigureAsync(bob, other.Revision, false, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(bob, other.Revision, proposal with { Source = new() { MessageIds = [id], Excerpt = "My actual personal syllabus" } }, true));
    }
}
