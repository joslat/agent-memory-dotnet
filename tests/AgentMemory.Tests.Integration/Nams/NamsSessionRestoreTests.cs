using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using NSubstitute;
using AgentMemory.AgentFramework;
using AgentMemory.AgentFramework.Nams;
using AgentMemory.Nams.Identity;
using AgentMemory.Nams.Recall;

namespace AgentMemory.Tests.Integration.Nams;

/// <summary>
/// Formalizes, as asserted live tests, what <c>AgentMemory.Sample.NamsAgent</c>'s console demo only shows
/// informally: that a MAF <see cref="AgentSession"/>'s NAMS conversation identity -- and therefore the real
/// NAMS-side conversation it maps to -- survives <c>SerializeSessionAsync</c>/<c>DeserializeSessionAsync</c>.
///
/// Both tests drive a REAL <see cref="ChatClientAgent"/> (via <c>AsAIAgent</c>) wired to a REAL, DI-resolved
/// <see cref="NamsMemoryContextProvider"/> against the live NAMS SaaS -- only the model call itself is
/// stubbed (an NSubstitute <see cref="IChatClient"/> returning a canned response), so there is no dependency
/// on a real LLM. This exercises MAF's own session-serialization machinery for real, not a hand-rolled
/// substitute for it (contrast <c>MemoryOwnerScopingAgentIntegrationTests</c>' <c>ScriptedInnerAgent</c>,
/// whose <c>SerializeSessionCoreAsync</c>/<c>DeserializeSessionCoreAsync</c> are trivial stubs -- unsuitable
/// here, since serialization behavior is exactly what's under test).
///
/// Deliberately does NOT assert on the messages the stub <see cref="IChatClient"/> receives: a probe of
/// <c>ChatClientAgent</c>'s real serialized-session JSON showed it embeds its own
/// <c>InMemoryChatHistoryProvider</c> turn history alongside the memory-identity state bag, so a restored
/// session's next turn would carry the prior turn's raw text as ordinary chat history regardless of whether
/// NAMS recall works at all -- a marker found in the outgoing request messages wouldn't distinguish "NAMS
/// recalled it" from "MAF's own history already had it". Also NOT assertable via a
/// <c>&lt;recalled_memory category="..."&gt;</c> wrapper search (<c>MemoryTraceChatClient</c>'s approach in
/// the sample): per <c>NamsMafTypeMapper.ToContextMessages</c>, the <c>RecentMessage</c>/<c>RelevantMessage</c>
/// categories a plain persisted turn recalls under are deliberately left undelimited ("a recalled message
/// renders as an individual conversation turn, not an injected block"), so they carry no distinguishing
/// wrapper either. Instead, each test independently confirms -- via a direct, live NAMS read through
/// <see cref="INamsRecallService"/>, entirely outside the agent pipeline -- that content persisted through
/// the RESTORED session's own real <c>RunAsync</c> call lands in the SAME NAMS conversation a marker
/// persisted before serialization did. This is non-trivial specifically because it is the RESTORED session's
/// own post-deserialize state (not a second direct call with the original identity values, which the shared
/// singleton <c>INamsConversationStateStore</c> would trivially agree with regardless of whether restore
/// works) that must resolve to that same conversation.
/// </summary>
[Collection("NAMS Live")]
[Trait("Category", "Integration")]
public sealed class NamsSessionRestoreTests
{
    private readonly NamsLiveFixture _fixture;

    public NamsSessionRestoreTests(NamsLiveFixture fixture) => _fixture = fixture;

    [LiveNamsFact]
    public async Task SessionRestore_WithMemoryIdentityReapplied_ContinuesPersistingToTheSameNamsConversation()
    {
        using var scope = _fixture.Services!.CreateScope();
        var sp = scope.ServiceProvider;
        var memoryProvider = sp.GetRequiredService<NamsMemoryContextProvider>();
        var resolver = sp.GetRequiredService<INamsConversationResolver>();
        var recall = sp.GetRequiredService<INamsRecallService>();

        var (applicationId, userId, sessionId, conversationId) = UniqueIds();
        var agent = CreateStubbedAgent(memoryProvider);

        var session = (await agent.CreateSessionAsync()).WithMemoryIdentity(userId, sessionId, conversationId, applicationId);
        var markerBeforeRestore = $"before-restore-{Guid.NewGuid():N}";
        await agent.RunAsync(markerBeforeRestore, session);

        var identity = new NamsConversationIdentity
        {
            ApplicationId = applicationId, UserId = userId, SessionId = sessionId, LocalConversationId = conversationId
        };
        var namsConversationId = (await resolver.ResolveAsync(identity, CancellationToken.None)).NamsConversationId;

        var indexedBeforeRestore = await PollUntilAsync(
            async () =>
            {
                var recalled = await recall.RecallAsync(namsConversationId, markerBeforeRestore, CancellationToken.None);
                return recalled.Items.Any(i => i.Content.Contains(markerBeforeRestore, StringComparison.Ordinal));
            },
            timeout: TimeSpan.FromSeconds(30));
        indexedBeforeRestore.Should().BeTrue("the pre-restore turn must actually be indexed before restore can meaningfully be proven to continue it");

        var serialized = await agent.SerializeSessionAsync(session);
        var restored = (await agent.DeserializeSessionAsync(serialized)).WithMemoryIdentity(userId, sessionId, conversationId, applicationId);
        restored.Should().NotBeSameAs(session, "this must be a genuine restore from serialized JSON, not the original in-memory session object");

        var markerAfterRestore = $"after-restore-{Guid.NewGuid():N}";
        await agent.RunAsync(markerAfterRestore, restored);

        var indexedAfterRestore = await PollUntilAsync(
            async () =>
            {
                var recalled = await recall.RecallAsync(namsConversationId, markerAfterRestore, CancellationToken.None);
                return recalled.Items.Any(i => i.Content.Contains(markerAfterRestore, StringComparison.Ordinal));
            },
            timeout: TimeSpan.FromSeconds(30));
        indexedAfterRestore.Should().BeTrue(
            "a turn run on the restored session (with memory identity re-applied, matching the NamsAgent sample's " +
            "documented recipe) must persist to the SAME NAMS conversation created before serialization, not a new one");
    }

    [LiveNamsFact]
    public async Task SessionRestore_WithoutReapplyingMemoryIdentity_StillPersistsToTheSameNamsConversation()
    {
        using var scope = _fixture.Services!.CreateScope();
        var sp = scope.ServiceProvider;
        var memoryProvider = sp.GetRequiredService<NamsMemoryContextProvider>();
        var resolver = sp.GetRequiredService<INamsConversationResolver>();
        var recall = sp.GetRequiredService<INamsRecallService>();

        var (applicationId, userId, sessionId, conversationId) = UniqueIds();
        var agent = CreateStubbedAgent(memoryProvider);

        var session = (await agent.CreateSessionAsync()).WithMemoryIdentity(userId, sessionId, conversationId, applicationId);
        var markerBeforeRestore = $"before-restore-noreapply-{Guid.NewGuid():N}";
        await agent.RunAsync(markerBeforeRestore, session);

        var identity = new NamsConversationIdentity
        {
            ApplicationId = applicationId, UserId = userId, SessionId = sessionId, LocalConversationId = conversationId
        };
        var namsConversationId = (await resolver.ResolveAsync(identity, CancellationToken.None)).NamsConversationId;

        var indexedBeforeRestore = await PollUntilAsync(
            async () =>
            {
                var recalled = await recall.RecallAsync(namsConversationId, markerBeforeRestore, CancellationToken.None);
                return recalled.Items.Any(i => i.Content.Contains(markerBeforeRestore, StringComparison.Ordinal));
            },
            timeout: TimeSpan.FromSeconds(30));
        indexedBeforeRestore.Should().BeTrue("the pre-restore turn must actually be indexed before restore can meaningfully be proven to continue it");

        // Deliberately skips re-applying WithMemoryIdentity after restore, unlike the NamsAgent sample's own
        // recipe and the sibling test above -- proving ChatClientAgent's own state-bag serialization already
        // carries the memory identity through on its own (confirmed by inspecting the real serialized JSON
        // before writing this test: it embeds user_id/session_id/conversation_id/application_id alongside its
        // own chat history), making the sample's re-stamp step defensive rather than strictly load-bearing.
        var serialized = await agent.SerializeSessionAsync(session);
        var restored = await agent.DeserializeSessionAsync(serialized);
        restored.Should().NotBeSameAs(session, "this must be a genuine restore from serialized JSON, not the original in-memory session object");

        var markerAfterRestore = $"after-restore-noreapply-{Guid.NewGuid():N}";
        await agent.RunAsync(markerAfterRestore, restored);

        var indexedAfterRestore = await PollUntilAsync(
            async () =>
            {
                var recalled = await recall.RecallAsync(namsConversationId, markerAfterRestore, CancellationToken.None);
                return recalled.Items.Any(i => i.Content.Contains(markerAfterRestore, StringComparison.Ordinal));
            },
            timeout: TimeSpan.FromSeconds(30));
        indexedAfterRestore.Should().BeTrue(
            "the memory identity survives ChatClientAgent's own session serialization without needing to be re-applied, " +
            "so a turn on the restored session must still persist to the SAME NAMS conversation created before serialization");
    }

    private static AIAgent CreateStubbedAgent(NamsMemoryContextProvider memoryProvider)
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Acknowledged."))));

        return chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "NamsSessionRestoreTestAgent",
            AIContextProviders = [memoryProvider],
        });
    }

    private static (string ApplicationId, string UserId, string SessionId, string ConversationId) UniqueIds() =>
    (
        "agent-memory-dotnet-live-tests",
        $"test-user-{Guid.NewGuid():N}",
        Guid.NewGuid().ToString("N"),
        Guid.NewGuid().ToString("N")
    );

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
