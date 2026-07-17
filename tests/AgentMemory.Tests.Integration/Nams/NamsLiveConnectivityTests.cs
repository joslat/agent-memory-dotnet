using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using AgentMemory.Nams.Identity;
using AgentMemory.Nams.Persistence;
using AgentMemory.Nams.Recall;

namespace AgentMemory.Tests.Integration.Nams;

/// <summary>
/// Live round-trip tests against the real NAMS SaaS (<c>memory.neo4jlabs.com</c>), exercised entirely
/// through the public surface (<see cref="INamsConversationResolver"/>, <see cref="INamsPersistenceService"/>,
/// <see cref="INamsRecallService"/>) -- the same contract <c>NamsMemoryContextProvider</c> (Phase 6) uses
/// internally. Every identity is a fresh GUID (engineering plan Phase 10: "unique test user prefix"), and
/// these only ever run against the isolated <c>agent-memory-dotnet-dev</c> workspace -- never a production
/// customer workspace. <see cref="LiveNamsFactAttribute"/> skips cleanly when
/// NAMS_API_KEY/NAMS_DEV_WORKSPACE_ID aren't configured -- CI still discovers these tests (they carry the
/// same <c>Category=Integration</c> trait as every other integration test), but they report as Skipped
/// there and never fail the build.
/// </summary>
[Collection("NAMS Live")]
[Trait("Category", "Integration")]
public sealed class NamsLiveConnectivityTests
{
    private readonly NamsLiveFixture _fixture;

    public NamsLiveConnectivityTests(NamsLiveFixture fixture) => _fixture = fixture;

    [LiveNamsFact]
    public async Task ResolveAsync_NewIdentity_CreatesConversation()
    {
        var resolver = _fixture.Services!.GetRequiredService<INamsConversationResolver>();

        var result = await resolver.ResolveAsync(UniqueIdentity(), CancellationToken.None);

        result.WasCreated.Should().BeTrue();
        result.NamsConversationId.Should().NotBeNullOrWhiteSpace();
    }

    [LiveNamsFact]
    public async Task PersistTurnAsync_ThenRecallAsync_EventuallyReturnsThePersistedMessage()
    {
        var services = _fixture.Services!;
        var resolver = services.GetRequiredService<INamsConversationResolver>();
        var persistence = services.GetRequiredService<INamsPersistenceService>();
        var recall = services.GetRequiredService<INamsRecallService>();

        var conversation = await resolver.ResolveAsync(UniqueIdentity(), CancellationToken.None);

        var marker = $"NAMS live test marker {Guid.NewGuid():N}: John works at Acme Corp in Denver.";
        var persistResult = await persistence.PersistTurnAsync(
            conversation.NamsConversationId,
            userMessages: [new NamsMessageToPersist(marker)],
            assistantMessages: [],
            CancellationToken.None);

        persistResult.Outcome.Should().Be(NamsPersistenceOutcome.Persisted);

        // Bounded poll -- NAMS ingestion/extraction is asynchronous. Never unbounded (Phase 10 release gate
        // explicitly rejects unbounded eventual-consistency waits).
        var found = await PollUntilAsync(
            async () =>
            {
                var recalled = await recall.RecallAsync(conversation.NamsConversationId, marker, CancellationToken.None);
                return recalled.Items.Any(i => i.Content.Contains(marker, StringComparison.Ordinal));
            },
            timeout: TimeSpan.FromSeconds(30));

        found.Should().BeTrue("the just-persisted message should surface in recall's recent-messages tier");
    }

    [LiveNamsFact]
    public async Task PersistTurnAsync_MessageWithKnownEntity_EventuallyExtractsIt()
    {
        var services = _fixture.Services!;
        var resolver = services.GetRequiredService<INamsConversationResolver>();
        var persistence = services.GetRequiredService<INamsPersistenceService>();
        var recall = services.GetRequiredService<INamsRecallService>();

        var conversation = await resolver.ResolveAsync(UniqueIdentity(), CancellationToken.None);
        var entityName = $"Acme-{Guid.NewGuid():N}".Substring(0, 12);

        await persistence.PersistTurnAsync(
            conversation.NamsConversationId,
            userMessages: [new NamsMessageToPersist($"John works at {entityName} Corp in Denver.")],
            assistantMessages: [],
            CancellationToken.None);

        // Entity extraction runs asynchronously server-side and can lag well behind ingestion.
        var found = await PollUntilAsync(
            async () =>
            {
                var recalled = await recall.RecallAsync(conversation.NamsConversationId, entityName, CancellationToken.None);
                return recalled.Items.Any(i => i.Content.Contains(entityName, StringComparison.Ordinal));
            },
            timeout: TimeSpan.FromSeconds(60));

        found.Should().BeTrue($"an entity named '{entityName}' should eventually be extracted and recallable");
    }

    [LiveNamsFact]
    public async Task ResolveAsync_SameIdentityResolvedConcurrently_ReconcilesToOneConversation()
    {
        var resolver = _fixture.Services!.GetRequiredService<INamsConversationResolver>();
        var identity = UniqueIdentity(); // one identity, resolved twice concurrently below

        var task1 = resolver.ResolveAsync(identity, CancellationToken.None);
        var task2 = resolver.ResolveAsync(identity, CancellationToken.None);
        var results = await Task.WhenAll(task1, task2);

        results[0].NamsConversationId.Should().Be(results[1].NamsConversationId,
            "both resolutions are for the same identity -- they must agree on one conversation");
        results.Count(r => r.WasCreated).Should().Be(1,
            "exactly one of the two concurrent resolutions should have actually created the conversation");
    }

    [LiveNamsFact]
    public async Task PersistTurnAsync_TwoConcurrentUsers_DoNotCrossContaminateChatHistory()
    {
        var services = _fixture.Services!;
        var resolver = services.GetRequiredService<INamsConversationResolver>();
        var persistence = services.GetRequiredService<INamsPersistenceService>();
        var recall = services.GetRequiredService<INamsRecallService>();

        var resolveTaskA = resolver.ResolveAsync(UniqueIdentity(), CancellationToken.None);
        var resolveTaskB = resolver.ResolveAsync(UniqueIdentity(), CancellationToken.None);
        await Task.WhenAll(resolveTaskA, resolveTaskB);
        var conversationA = await resolveTaskA;
        var conversationB = await resolveTaskB;

        var markerA = $"Concurrency test A {Guid.NewGuid():N}";
        var markerB = $"Concurrency test B {Guid.NewGuid():N}";
        await Task.WhenAll(
            persistence.PersistTurnAsync(conversationA.NamsConversationId, [new NamsMessageToPersist(markerA)], [], CancellationToken.None),
            persistence.PersistTurnAsync(conversationB.NamsConversationId, [new NamsMessageToPersist(markerB)], [], CancellationToken.None));

        var found = await PollUntilAsync(
            async () =>
            {
                var recalledA = await recall.RecallAsync(conversationA.NamsConversationId, markerA, CancellationToken.None);
                var recalledB = await recall.RecallAsync(conversationB.NamsConversationId, markerB, CancellationToken.None);
                var aHasOwnMarker = recalledA.Items.Any(i => i.Content.Contains(markerA, StringComparison.Ordinal));
                var bHasOwnMarker = recalledB.Items.Any(i => i.Content.Contains(markerB, StringComparison.Ordinal));
                var noCrossContamination =
                    !recalledA.Items.Any(i => i.Content.Contains(markerB, StringComparison.Ordinal))
                    && !recalledB.Items.Any(i => i.Content.Contains(markerA, StringComparison.Ordinal));
                return aHasOwnMarker && bHasOwnMarker && noCrossContamination;
            },
            timeout: TimeSpan.FromSeconds(30));

        found.Should().BeTrue("each conversation's recall should see only its own persisted message, never the other's");
    }

    [LiveNamsFact]
    public async Task RecallAsync_CancelledBeforeTheHttpRoundTripCompletes_PropagatesOperationCanceledException()
    {
        var services = _fixture.Services!;
        var resolver = services.GetRequiredService<INamsConversationResolver>();
        var recall = services.GetRequiredService<INamsRecallService>();
        var conversation = await resolver.ResolveAsync(UniqueIdentity(), CancellationToken.None);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(1)); // a live round trip to NAMS never completes this fast

        var act = () => recall.RecallAsync(conversation.NamsConversationId, "test", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [LiveNamsFact]
    public async Task PersistTurnAsync_UnicodeAndEmojiContent_RoundTripsByteForByte()
    {
        var services = _fixture.Services!;
        var resolver = services.GetRequiredService<INamsConversationResolver>();
        var persistence = services.GetRequiredService<INamsPersistenceService>();
        var recall = services.GetRequiredService<INamsRecallService>();
        var conversation = await resolver.ResolveAsync(UniqueIdentity(), CancellationToken.None);

        var marker = $"Unicode test {Guid.NewGuid():N}: 日本語のテスト 🎉 émoji café";
        await persistence.PersistTurnAsync(
            conversation.NamsConversationId, userMessages: [new NamsMessageToPersist(marker)], assistantMessages: [], CancellationToken.None);

        var found = await PollUntilAsync(
            async () =>
            {
                var recalled = await recall.RecallAsync(conversation.NamsConversationId, marker, CancellationToken.None);
                return recalled.Items.Any(i => i.Content.Contains(marker, StringComparison.Ordinal));
            },
            timeout: TimeSpan.FromSeconds(30));

        found.Should().BeTrue("Unicode/emoji content should round-trip through NAMS byte-for-byte");
    }

    private static NamsConversationIdentity UniqueIdentity([CallerMemberName] string testName = "") => new()
    {
        ApplicationId = "agent-memory-dotnet-live-tests",
        UserId = $"test-{testName}-{Guid.NewGuid():N}",
        SessionId = Guid.NewGuid().ToString("N"),
        LocalConversationId = Guid.NewGuid().ToString("N")
    };

    /// <summary>
    /// Bounded poll helper -- never waits longer than <paramref name="timeout"/>. Swallows exceptions from
    /// <paramref name="condition"/> itself (a live HTTP call can hit a transient blip mid-poll) and keeps
    /// retrying within the remaining budget, matching <c>Neo4jIntegrationFixture.WaitForVectorIndexesAsync</c>'s
    /// established pattern for this repo's other eventual-consistency polls.
    /// </summary>
    private static async Task<bool> PollUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            try
            {
                if (await condition())
                    return true;
            }
            catch { /* transient failure against a live external service -- ignore and keep polling */ }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
        return false;
    }
}
