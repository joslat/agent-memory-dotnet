using FluentAssertions;
using AgentMemory.Nams.Identity;

namespace AgentMemory.Tests.Unit.Nams;

public sealed class InMemoryNamsConversationStateStoreTests
{
    [Fact]
    public async Task TryGetAsync_AbsentKey_ReturnsNull()
    {
        var store = new InMemoryNamsConversationStateStore();

        var result = await store.TryGetAsync("session", "local", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task TryCreateAsync_FirstCallSucceeds_SecondCallForSameKeyFails()
    {
        var store = new InMemoryNamsConversationStateStore();
        var identity = new NamsConversationIdentity
        {
            ApplicationId = "app", UserId = "user", SessionId = "session", LocalConversationId = "local",
            NamsConversationId = "conv-1"
        };

        var firstResult = await store.TryCreateAsync(identity, CancellationToken.None);
        var secondResult = await store.TryCreateAsync(identity with { NamsConversationId = "conv-2" }, CancellationToken.None);

        firstResult.Should().BeTrue();
        secondResult.Should().BeFalse();
        var stored = await store.TryGetAsync("session", "local", CancellationToken.None);
        stored!.NamsConversationId.Should().Be("conv-1"); // the second write never overwrote the first
    }

    [Fact]
    public async Task TryCreateAsync_ConcurrentCallsSameKey_ExactlyOneSucceeds()
    {
        var store = new InMemoryNamsConversationStateStore();
        var tasks = Enumerable.Range(0, 50).Select(i => store.TryCreateAsync(
            new NamsConversationIdentity
            {
                ApplicationId = "app", UserId = "user", SessionId = "session", LocalConversationId = "local",
                NamsConversationId = $"conv-{i}"
            },
            CancellationToken.None));

        var results = await Task.WhenAll(tasks);

        results.Count(r => r).Should().Be(1);
    }

    [Fact]
    public async Task DifferentSessionsOrLocalConversationIds_DoNotCollide()
    {
        var store = new InMemoryNamsConversationStateStore();
        var identityA = new NamsConversationIdentity
        {
            ApplicationId = "app", UserId = "user", SessionId = "session-a", LocalConversationId = "local",
            NamsConversationId = "conv-a"
        };
        var identityB = identityA with { SessionId = "session-b", NamsConversationId = "conv-b" };

        (await store.TryCreateAsync(identityA, CancellationToken.None)).Should().BeTrue();
        (await store.TryCreateAsync(identityB, CancellationToken.None)).Should().BeTrue();

        (await store.TryGetAsync("session-a", "local", CancellationToken.None))!.NamsConversationId.Should().Be("conv-a");
        (await store.TryGetAsync("session-b", "local", CancellationToken.None))!.NamsConversationId.Should().Be("conv-b");
    }

    [Fact]
    public async Task DifferentUsers_SameSessionAndLocalConversationId_DoCollide()
    {
        // Deliberate: the store key is (session, local-conversation) only. A session/local-conversation slot
        // is one binding; a different user targeting the same slot must collide at the storage layer so the
        // resolver can detect and reject it, rather than each user silently getting their own mapping.
        var store = new InMemoryNamsConversationStateStore();
        var identityA = new NamsConversationIdentity
        {
            ApplicationId = "app", UserId = "user-a", SessionId = "session", LocalConversationId = "local",
            NamsConversationId = "conv-a"
        };
        var identityB = identityA with { UserId = "user-b", NamsConversationId = "conv-b" };

        (await store.TryCreateAsync(identityA, CancellationToken.None)).Should().BeTrue();
        (await store.TryCreateAsync(identityB, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task DifferentApplications_SameSessionAndLocalConversationId_DoCollide()
    {
        // Same reasoning as the user case above, for application -- both are validated as data by the
        // resolver, not partitioned by the key (engineering plan: "changed application rejects stale mapping").
        var store = new InMemoryNamsConversationStateStore();
        var identityA = new NamsConversationIdentity
        {
            ApplicationId = "app-a", UserId = "user", SessionId = "session", LocalConversationId = "local",
            NamsConversationId = "conv-a"
        };
        var identityB = identityA with { ApplicationId = "app-b", NamsConversationId = "conv-b" };

        (await store.TryCreateAsync(identityA, CancellationToken.None)).Should().BeTrue();
        (await store.TryCreateAsync(identityB, CancellationToken.None)).Should().BeFalse();
    }
}
