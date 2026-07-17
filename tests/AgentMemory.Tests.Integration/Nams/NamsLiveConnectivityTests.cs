using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using AgentMemory.Nams.Client;
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

    /// <summary>
    /// Proves the single-process path only: the shared <c>KeyedAsyncLock</c> serializes the second call
    /// behind the first, which then takes the already-populated-store fast path -- it never reaches
    /// <c>CreateConversationAsync</c> itself. This does NOT exercise the cross-process "lost the race,
    /// reconcile an orphaned conversation" branch (that needs two separate resolver instances/processes
    /// racing against a genuinely shared store -- out of scope here, see the Phase 10 planning doc's
    /// "Multi-instance mapping" deferral).
    /// </summary>
    [LiveNamsFact]
    public async Task ResolveAsync_SameIdentityResolvedConcurrently_SerializesToOneConversation()
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

    /// <summary>
    /// Deliberately scoped to the <see cref="NamsRecallCategory.RecentMessage"/> tier only (conversation-
    /// scoped) -- NAMS entity search is workspace-wide, not conversation-scoped (documented in
    /// <c>AgentMemory.Sample.NamsAgent</c>'s README), so including entity-tier items here would make this
    /// test noisy/flaky for a reason unrelated to what it's actually verifying.
    /// </summary>
    [LiveNamsFact]
    public async Task PersistTurnAsync_TwoConcurrentUsers_EventuallyRecallOnlyTheirOwnRecentMessage()
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
                var recentA = recalledA.Items.Where(i => i.Category == NamsRecallCategory.RecentMessage).ToList();
                var recentB = recalledB.Items.Where(i => i.Category == NamsRecallCategory.RecentMessage).ToList();
                var aHasOwnMarker = recentA.Any(i => i.Content.Contains(markerA, StringComparison.Ordinal));
                var bHasOwnMarker = recentB.Any(i => i.Content.Contains(markerB, StringComparison.Ordinal));
                var noCrossContamination =
                    !recentA.Any(i => i.Content.Contains(markerB, StringComparison.Ordinal))
                    && !recentB.Any(i => i.Content.Contains(markerA, StringComparison.Ordinal));
                return aHasOwnMarker && bHasOwnMarker && noCrossContamination;
            },
            timeout: TimeSpan.FromSeconds(30));

        found.Should().BeTrue("each conversation's recent-message tier should show only its own persisted message, never the other's");
    }

    [LiveNamsFact]
    public async Task RecallAsync_CancelledBeforeTheHttpRoundTripCompletes_PropagatesOperationCanceledException()
    {
        var services = _fixture.Services!;
        var resolver = services.GetRequiredService<INamsConversationResolver>();
        var recall = services.GetRequiredService<INamsRecallService>();
        var conversation = await resolver.ResolveAsync(UniqueIdentity(), CancellationToken.None);

        using var cts = new CancellationTokenSource();
        // Environment assumption, not a language/runtime guarantee: this session's observed live NAMS
        // latency is consistently 400-1000ms, so 1ms reliably cancels first. If NAMS round-trips ever get
        // fast enough to race this, this test (not the production code) would need a longer delay.
        cts.CancelAfter(TimeSpan.FromMilliseconds(1));

        var act = () => recall.RecallAsync(conversation.NamsConversationId, "test", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// Matches only <see cref="NamsRecallCategory.RecentMessage"/> items -- <see cref="MapMessage"/>-mapped
    /// (in <c>NamsRecallService</c>) recent messages pass <c>Content</c> through verbatim, so a match there
    /// genuinely proves a byte-for-byte round trip. Reflections/observations are NAMS-synthesized text, not
    /// guaranteed to preserve the original bytes -- matching against those tiers too would make "byte for
    /// byte" a probabilistic claim rather than a proven one.
    /// </summary>
    [LiveNamsFact]
    public async Task PersistTurnAsync_UnicodeAndEmojiContent_EventuallyRoundTripsByteForByteInRecentMessages()
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
                return recalled.Items.Any(i =>
                    i.Category == NamsRecallCategory.RecentMessage && i.Content.Contains(marker, StringComparison.Ordinal));
            },
            timeout: TimeSpan.FromSeconds(30));

        found.Should().BeTrue("Unicode/emoji content should round-trip through NAMS byte-for-byte in the recent-messages tier");
    }

    // ── Phase 10: payload edge cases (empty / max size / multi-message / non-text-like) ─────────────

    [LiveNamsFact]
    public async Task PersistTurnAsync_EmptyContent_BulkEndpointAcceptsIt()
    {
        var services = _fixture.Services!;
        var resolver = services.GetRequiredService<INamsConversationResolver>();
        var persistence = services.GetRequiredService<INamsPersistenceService>();
        var conversation = await resolver.ResolveAsync(UniqueIdentity(), CancellationToken.None);

        // Genuine NAMS API inconsistency, confirmed live: the single-message endpoint
        // (POST /v1/conversations/{id}/messages) rejects empty content with 400, but the bulk endpoint
        // (POST /v1/conversations/{id}/messages/bulk) -- the ONLY one PersistTurnAsync ever calls --
        // accepts it and returns a real message ID. Documenting actual behavior, not the (wrong) assumption
        // that empty content fails uniformly across both NAMS endpoints.
        var result = await persistence.PersistTurnAsync(
            conversation.NamsConversationId, userMessages: [new NamsMessageToPersist("")], assistantMessages: [], CancellationToken.None);

        result.Outcome.Should().Be(NamsPersistenceOutcome.Persisted);
        result.PersistedMessageIds.Should().ContainSingle();
    }

    [LiveNamsFact]
    public async Task PersistTurnAsync_LargeMessage_PersistsSuccessfully_AndEventuallyRoundTripsWithinTheDefaultRecallBudget()
    {
        var services = _fixture.Services!;
        var resolver = services.GetRequiredService<INamsConversationResolver>();
        var persistence = services.GetRequiredService<INamsPersistenceService>();
        var recall = services.GetRequiredService<INamsRecallService>();
        var conversation = await resolver.ResolveAsync(UniqueIdentity(), CancellationToken.None);

        // Confirmed live: NAMS's write path accepts at least 50,000 characters in one message (separately
        // verified, not asserted here to keep this test's own runtime reasonable). For the round-trip half
        // of this test, deliberately use a size comfortably under NamsRecallOptions.MaxTotalCharacters'
        // default (8,000) -- a single item larger than the whole budget is silently excluded by
        // NamsRecallService's own ApplyCharacterBudget (correct, existing, separately-tested behavior), which
        // would make this test measure our own truncation logic instead of NAMS's payload handling.
        var marker = $"large-payload-{Guid.NewGuid():N} ";
        var largeContent = marker + new string('x', 5_000);
        var persistResult = await persistence.PersistTurnAsync(
            conversation.NamsConversationId, userMessages: [new NamsMessageToPersist(largeContent)], assistantMessages: [], CancellationToken.None);

        persistResult.Outcome.Should().Be(NamsPersistenceOutcome.Persisted);

        var found = await PollUntilAsync(
            async () =>
            {
                var recalled = await recall.RecallAsync(conversation.NamsConversationId, marker, CancellationToken.None);
                return recalled.Items.Any(i =>
                    i.Category == NamsRecallCategory.RecentMessage && i.Content.Contains(marker, StringComparison.Ordinal));
            },
            timeout: TimeSpan.FromSeconds(30));

        found.Should().BeTrue("a 5,000-character message (within the default recall budget) should round-trip through NAMS");
    }

    /// <summary>
    /// Deliberately does NOT assert message ordering -- <see cref="NamsPersistenceResult.PersistedMessageIds"/>
    /// is a bare <c>IReadOnlyList&lt;string&gt;</c> with no content/role correlation, and NAMS's own bulk-add
    /// response ordering guarantee (if any) isn't independently confirmed, so this can only prove all 4
    /// messages in one turn reach NAMS via the one bulk call, not that they arrive in submission order.
    /// </summary>
    [LiveNamsFact]
    public async Task PersistTurnAsync_MultipleMessagesInOneTurn_PersistsAllFour()
    {
        var services = _fixture.Services!;
        var resolver = services.GetRequiredService<INamsConversationResolver>();
        var persistence = services.GetRequiredService<INamsPersistenceService>();
        var conversation = await resolver.ResolveAsync(UniqueIdentity(), CancellationToken.None);

        var id = Guid.NewGuid().ToString("N");
        var result = await persistence.PersistTurnAsync(
            conversation.NamsConversationId,
            userMessages: [new NamsMessageToPersist($"user-1-{id}"), new NamsMessageToPersist($"user-2-{id}")],
            assistantMessages: [new NamsMessageToPersist($"assistant-1-{id}"), new NamsMessageToPersist($"assistant-2-{id}")],
            CancellationToken.None);

        result.Outcome.Should().Be(NamsPersistenceOutcome.Persisted);
        result.PersistedMessageIds.Should().HaveCount(4, "all 4 messages in the turn should be persisted in one bulk call");
    }

    [LiveNamsFact]
    public async Task PersistTurnAsync_CodeLikeContentWithSpecialCharacters_EventuallyRoundTrips()
    {
        var services = _fixture.Services!;
        var resolver = services.GetRequiredService<INamsConversationResolver>();
        var persistence = services.GetRequiredService<INamsPersistenceService>();
        var recall = services.GetRequiredService<INamsRecallService>();
        var conversation = await resolver.ResolveAsync(UniqueIdentity(), CancellationToken.None);

        // "Non-text" per the plan's payload matrix, interpreted as structured/code-like text content --
        // NamsMessageToPersist.Content is a string; NAMS has no binary/attachment concept to test against.
        var marker = Guid.NewGuid().ToString("N");
        var codeContent = $$"""
            marker: {{marker}}
            ```csharp
            var x = new Dictionary<string, string> { ["a"] = "b\n\tc" };
            Console.WriteLine($"{x["a"]}");
            ```
            """;
        await persistence.PersistTurnAsync(
            conversation.NamsConversationId, userMessages: [new NamsMessageToPersist(codeContent)], assistantMessages: [], CancellationToken.None);

        var found = await PollUntilAsync(
            async () =>
            {
                var recalled = await recall.RecallAsync(conversation.NamsConversationId, marker, CancellationToken.None);
                return recalled.Items.Any(i =>
                    i.Category == NamsRecallCategory.RecentMessage && i.Content.Contains(marker, StringComparison.Ordinal));
            },
            timeout: TimeSpan.FromSeconds(30));

        found.Should().BeTrue("code-like content with braces/quotes/newlines should round-trip through NAMS");
    }

    // ── Phase 10b: data lifecycle -- delete conversation ──────────────────────────────────────────
    // INamsClient is internal (see AgentMemory.Nams.csproj's InternalsVisibleTo grant to this project) --
    // DeleteConversationAsync is deliberately not exposed via any public service (INamsPersistenceService/
    // INamsConversationResolver), so these tests reach the low-level client directly.

    [LiveNamsFact]
    public async Task DeleteConversationAsync_ThenGetContext_ReturnsEmptyTiers_AndFurtherWritesFail()
    {
        var services = _fixture.Services!;
        var resolver = services.GetRequiredService<INamsConversationResolver>();
        var persistence = services.GetRequiredService<INamsPersistenceService>();
        var namsClient = services.GetRequiredService<INamsClient>();
        var conversation = await resolver.ResolveAsync(UniqueIdentity(), CancellationToken.None);

        await persistence.PersistTurnAsync(
            conversation.NamsConversationId, userMessages: [new NamsMessageToPersist("hello before delete")], assistantMessages: [], CancellationToken.None);

        var deleteAct = () => namsClient.DeleteConversationAsync(conversation.NamsConversationId, CancellationToken.None);
        await deleteAct.Should().NotThrowAsync();

        // Confirmed live: GetContext keeps returning 200 with empty tiers after delete -- never 404.
        var context = await namsClient.GetContextAsync(conversation.NamsConversationId, CancellationToken.None);
        context.RecentMessages.Should().BeEmpty();
        context.Observations.Should().BeEmpty();
        context.Reflections.Should().BeEmpty();

        // Confirmed live: adding messages (the only write path PersistTurnAsync uses) 404s after delete --
        // NamsPersistenceService classifies a 404 as Failed (not UnknownWriteOutcome, which is reserved for
        // genuinely ambiguous Network/Timeout failures -- a 404 is an unambiguous "this doesn't exist").
        var writeAfterDelete = await persistence.PersistTurnAsync(
            conversation.NamsConversationId, userMessages: [new NamsMessageToPersist("after delete")], assistantMessages: [], CancellationToken.None);
        writeAfterDelete.Outcome.Should().Be(NamsPersistenceOutcome.Failed, "the conversation no longer exists to write to");
    }

    [LiveNamsFact]
    public async Task DeleteConversationAsync_CalledTwice_IsIdempotent()
    {
        var services = _fixture.Services!;
        var resolver = services.GetRequiredService<INamsConversationResolver>();
        var namsClient = services.GetRequiredService<INamsClient>();
        var conversation = await resolver.ResolveAsync(UniqueIdentity(), CancellationToken.None);

        var act = async () =>
        {
            await namsClient.DeleteConversationAsync(conversation.NamsConversationId, CancellationToken.None);
            await namsClient.DeleteConversationAsync(conversation.NamsConversationId, CancellationToken.None);
        };

        await act.Should().NotThrowAsync("confirmed live that deleting an already-deleted conversation still succeeds, not 404s");
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
