using System.Collections.Concurrent;
using FluentAssertions;
using AgentMemory.Neo4j.Infrastructure;

namespace AgentMemory.Tests.Unit.Infrastructure;

public sealed class MemoryStoreContextTests
{
    [Fact]
    public void ApplicationId_DefaultsToNull()
    {
        new DefaultMemoryStoreContext().ApplicationId.Should().BeNull();
    }

    [Fact]
    public void ApplicationId_RoundTrips()
    {
        var ctx = new DefaultMemoryStoreContext { ApplicationId = "acme" };
        ctx.ApplicationId.Should().Be("acme");
    }

    [Fact]
    public async Task ApplicationId_IsIsolatedPerAsyncFlow_NoCrossRequestBleed()
    {
        // The store context is registered as a singleton but mutated per request (R1b / IC6). Backed
        // by AsyncLocal, concurrent flows that each set a different ApplicationId must not corrupt one
        // another's routing — the captive-singleton defect this fix closes.
        var ctx = new DefaultMemoryStoreContext();
        var observed = new ConcurrentDictionary<string, string?>();

        async Task Flow(string id)
        {
            ctx.ApplicationId = id;
            await Task.Delay(25);           // overlap with the other flows
            observed[id] = ctx.ApplicationId; // must still be this flow's id
        }

        await Task.WhenAll(
            Task.Run(() => Flow("alice-app")),
            Task.Run(() => Flow("bob-app")),
            Task.Run(() => Flow("carol-app")));

        observed["alice-app"].Should().Be("alice-app");
        observed["bob-app"].Should().Be("bob-app");
        observed["carol-app"].Should().Be("carol-app");
    }
}
