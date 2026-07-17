using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Nams.Client;
using AgentMemory.Nams.Domain;
using AgentMemory.Nams.Identity;

namespace AgentMemory.Tests.Unit.Nams;

public sealed class NamsConversationResolverTests
{
    private sealed class FakeNamsClient : INamsClient
    {
        private int _nextId;
        public ConcurrentBag<string?> CreatedForUserIds { get; } = [];
        public List<IReadOnlyDictionary<string, string>?> CapturedMetadata { get; } = [];
        public Func<Task>? BeforeCreate { get; set; }
        public Exception? ThrowOnCreate { get; set; }

        public async Task<NamsConversation> CreateConversationAsync(
            string? userId, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BeforeCreate is not null)
                await BeforeCreate();
            if (ThrowOnCreate is not null)
                throw ThrowOnCreate;

            CreatedForUserIds.Add(userId);
            lock (CapturedMetadata)
                CapturedMetadata.Add(metadata);
            var id = $"conv-{Interlocked.Increment(ref _nextId)}";
            return new NamsConversation(id, "ws-1", userId, null);
        }

        public Task<NamsContext> GetContextAsync(string conversationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<NamsMessage>> AddMessagesAsync(
            string conversationId, IReadOnlyList<NamsMessageInput> messages, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<NamsEntity>> SearchEntitiesAsync(
            string query, string? type, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<NamsMessage>> SearchMessagesAsync(
            string conversationId, string query, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<NamsEntity>> ListEntitiesAsync(int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static NamsConversationIdentity Identity(
        string app = "app-1", string user = "user-1", string session = "session-1", string local = "local-1") =>
        new() { ApplicationId = app, UserId = user, SessionId = session, LocalConversationId = local };

    private static NamsConversationResolver CreateResolver(FakeNamsClient client, INamsConversationStateStore store) =>
        new(client, store, NullLogger<NamsConversationResolver>.Instance);

    [Fact]
    public async Task FirstCall_CreatesOneConversation()
    {
        var client = new FakeNamsClient();
        var resolver = CreateResolver(client, new InMemoryNamsConversationStateStore());

        var result = await resolver.ResolveAsync(Identity(), CancellationToken.None);

        result.WasCreated.Should().BeTrue();
        result.NamsConversationId.Should().Be("conv-1");
        client.CreatedForUserIds.Should().ContainSingle();
    }

    [Fact]
    public async Task RepeatedCall_ReusesExistingMapping_DoesNotCreateAgain()
    {
        var client = new FakeNamsClient();
        var resolver = CreateResolver(client, new InMemoryNamsConversationStateStore());
        var identity = Identity();

        var first = await resolver.ResolveAsync(identity, CancellationToken.None);
        var second = await resolver.ResolveAsync(identity, CancellationToken.None);

        second.WasCreated.Should().BeFalse();
        second.NamsConversationId.Should().Be(first.NamsConversationId);
        client.CreatedForUserIds.Should().ContainSingle();
    }

    [Fact]
    public async Task ParallelCalls_SameResolverInstance_CreateOnlyOneConversation()
    {
        var client = new FakeNamsClient();
        var resolver = CreateResolver(client, new InMemoryNamsConversationStateStore());
        var identity = Identity();

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => resolver.ResolveAsync(identity, CancellationToken.None)));

        client.CreatedForUserIds.Should().ContainSingle();
        results.Select(r => r.NamsConversationId).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task RestoredSession_SecondResolverInstance_ReusesMapping()
    {
        var sharedStore = new InMemoryNamsConversationStateStore();
        var client = new FakeNamsClient();
        var firstResolver = CreateResolver(client, sharedStore);
        var identity = Identity();
        var first = await firstResolver.ResolveAsync(identity, CancellationToken.None);

        var secondResolver = CreateResolver(client, sharedStore); // simulates a fresh process/restored session
        var second = await secondResolver.ResolveAsync(identity, CancellationToken.None);

        second.WasCreated.Should().BeFalse();
        second.NamsConversationId.Should().Be(first.NamsConversationId);
        client.CreatedForUserIds.Should().ContainSingle();
    }

    [Fact]
    public async Task ChangedUser_ThrowsIdentityConflictException()
    {
        var client = new FakeNamsClient();
        var resolver = CreateResolver(client, new InMemoryNamsConversationStateStore());
        await resolver.ResolveAsync(Identity(user: "user-1"), CancellationToken.None);

        var act = () => resolver.ResolveAsync(Identity(user: "user-2"), CancellationToken.None);

        await act.Should().ThrowAsync<NamsConversationIdentityConflictException>();
    }

    [Fact]
    public async Task ChangedApplication_ThrowsIdentityConflictException()
    {
        var client = new FakeNamsClient();
        var resolver = CreateResolver(client, new InMemoryNamsConversationStateStore());
        await resolver.ResolveAsync(Identity(app: "app-1"), CancellationToken.None);

        var act = () => resolver.ResolveAsync(Identity(app: "app-2"), CancellationToken.None);

        await act.Should().ThrowAsync<NamsConversationIdentityConflictException>();
    }

    [Theory]
    [InlineData("", "user", "session", "local")]
    [InlineData("app", "", "session", "local")]
    [InlineData("app", "user", "", "local")]
    [InlineData("app", "user", "session", "")]
    public async Task MissingIdentityField_ThrowsArgumentException(string app, string user, string session, string local)
    {
        var resolver = CreateResolver(new FakeNamsClient(), new InMemoryNamsConversationStateStore());
        var identity = new NamsConversationIdentity
        {
            ApplicationId = app, UserId = user, SessionId = session, LocalConversationId = local
        };

        var act = () => resolver.ResolveAsync(identity, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CreationFailure_LeavesStateUnchanged()
    {
        var client = new FakeNamsClient { ThrowOnCreate = new NamsOperationException(NamsFailureKind.ServerError, "boom") };
        var store = new InMemoryNamsConversationStateStore();
        var resolver = CreateResolver(client, store);

        var act = () => resolver.ResolveAsync(Identity(), CancellationToken.None);

        await act.Should().ThrowAsync<NamsOperationException>();
        (await store.TryGetAsync("session-1", "local-1", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Cancellation_PropagatesAndLeavesStateUnchanged()
    {
        var client = new FakeNamsClient();
        var store = new InMemoryNamsConversationStateStore();
        var resolver = CreateResolver(client, store);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => resolver.ResolveAsync(Identity(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await store.TryGetAsync("session-1", "local-1", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task BlankSavedConversationId_IsDetected_ThrowsInsteadOfSilentlyProceeding()
    {
        // A blank stored ID is a corrupted entry the store's add-if-absent contract can never self-heal (a
        // doomed creation attempt would just collide on the same occupied key) -- the plan's "invalid saved
        // ID is detected" requirement is satisfied by failing loudly and immediately, not by silently
        // creating a duplicate NAMS conversation that could never be persisted anyway.
        var store = new InMemoryNamsConversationStateStore();
        await store.TryCreateAsync(Identity() with { NamsConversationId = "   " }, CancellationToken.None);
        var client = new FakeNamsClient();
        var resolver = CreateResolver(client, store);

        var act = () => resolver.ResolveAsync(Identity(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        client.CreatedForUserIds.Should().BeEmpty(); // never even attempted -- the corrupted read is fatal immediately
    }

    [Fact]
    public async Task CorrelationMetadata_ContainsExpectedKeysAndValuesOnly()
    {
        var client = new FakeNamsClient();
        var resolver = CreateResolver(client, new InMemoryNamsConversationStateStore());

        await resolver.ResolveAsync(Identity(app: "app-x", session: "session-x", local: "local-x"), CancellationToken.None);

        var metadata = client.CapturedMetadata.Single()!;
        metadata.Should().ContainKey("agentMemoryApplicationId").WhoseValue.Should().Be("app-x");
        metadata.Should().ContainKey("agentMemorySessionId").WhoseValue.Should().Be("session-x");
        metadata.Should().ContainKey("agentMemoryConversationId").WhoseValue.Should().Be("local-x");
        metadata.Should().ContainKey("integration").WhoseValue.Should().Be("AgentMemory.NET");
        metadata.Should().ContainKey("integrationVersion");
        metadata.Should().HaveCount(5); // no unexpected extra keys
    }

    [Fact]
    public async Task NoCrossUserLeakage_ConcurrentDifferentUsersSameSession_OnlyOneSucceedsRestRejected()
    {
        // The store/lock key is (application, session, local-conversation) -- not user -- so N different
        // users racing for the SAME session/local-conversation slot contend for ONE binding. Exactly one may
        // win it; every other user's request must be rejected (NamsConversationIdentityConflictException),
        // never silently handed someone else's conversation ID and never silently given its own separate one.
        var client = new FakeNamsClient();
        var resolver = CreateResolver(client, new InMemoryNamsConversationStateStore());
        var users = Enumerable.Range(0, 10).Select(i => $"user-{i}").ToList();

        var outcomes = await Task.WhenAll(users.Select(async u =>
        {
            try
            {
                var result = await resolver.ResolveAsync(
                    Identity(user: u, session: "shared-session", local: "shared-local"), CancellationToken.None);
                return (Succeeded: true, result.NamsConversationId);
            }
            catch (NamsConversationIdentityConflictException)
            {
                return (Succeeded: false, NamsConversationId: (string?)null);
            }
        }));

        outcomes.Count(o => o.Succeeded).Should().Be(1);
        outcomes.Where(o => !o.Succeeded).Should().HaveCount(9);
    }

    [Fact]
    public async Task TwoIndependentResolvers_SharedStore_ConvergeOnOneMapping()
    {
        // Simulates two independent processes (or a process-restart crash window): each resolver has its own
        // process-local KeyedAsyncLock, so both may call CreateConversationAsync concurrently, but they share
        // one durable store -- the plan's own accepted residual duplicate-NAMS-conversation risk, mitigated
        // by both callers converging on the same winning mapping rather than diverging.
        var sharedStore = new InMemoryNamsConversationStateStore();
        var client = new FakeNamsClient();
        var barrier = new TaskCompletionSource();
        client.BeforeCreate = () => barrier.Task; // hold both creations until released, forcing an actual race
        var resolverA = CreateResolver(client, sharedStore);
        var resolverB = CreateResolver(client, sharedStore);
        var identity = Identity();

        var taskA = resolverA.ResolveAsync(identity, CancellationToken.None);
        var taskB = resolverB.ResolveAsync(identity, CancellationToken.None);
        await Task.Delay(50); // let both reach BeforeCreate concurrently
        barrier.SetResult();

        var results = await Task.WhenAll(taskA, taskB);

        results[0].NamsConversationId.Should().Be(results[1].NamsConversationId);
        results.Count(r => r.WasCreated).Should().Be(1); // the other reconciled onto the winner
    }
}
