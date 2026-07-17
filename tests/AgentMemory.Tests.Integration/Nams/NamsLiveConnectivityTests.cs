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
