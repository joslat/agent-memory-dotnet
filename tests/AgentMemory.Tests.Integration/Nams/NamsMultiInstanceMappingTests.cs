using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using AgentMemory.Nams.Client;
using AgentMemory.Nams.Identity;

namespace AgentMemory.Tests.Integration.Nams;

/// <summary>
/// Live tests for the cross-process conversation-mapping reconciliation path
/// (<see cref="NamsConversationResolver"/>'s own doc comment: "on a lost cross-store race, reconcile onto
/// the winning mapping"). <see cref="NamsLiveConnectivityTests"/>'s own single-resolver concurrency test can
/// only prove in-process lock serialization -- its own doc comment says so explicitly -- because one
/// resolver instance's <c>KeyedAsyncLock</c> fully serializes calls against itself. These tests construct
/// TWO independent <see cref="NamsConversationResolver"/> instances (each with its own process-local lock,
/// simulating two separate host processes) sharing ONE <see cref="InMemoryNamsConversationStateStore"/>
/// instance (simulating a durable store two real processes would share), so neither resolver's lock blocks
/// the other -- only the shared store's atomic add-if-absent can pick a winner.
///
/// Uses only real live NAMS calls (via the shared <see cref="INamsClient"/> resolved from the fixture's DI
/// container) -- both racing resolvers genuinely create their own NAMS conversation via
/// <c>CreateConversationAsync</c> before the shared store picks a winner, so this deliberately produces one
/// orphaned NAMS-side conversation per race -- the exact "residual duplicate-conversation risk" the
/// production code's own comment says the plan accepts rather than claiming an exactly-once guarantee NAMS
/// itself doesn't confirm.
/// </summary>
[Collection("NAMS Live")]
[Trait("Category", "Integration")]
public sealed class NamsMultiInstanceMappingTests
{
    private readonly NamsLiveFixture _fixture;

    public NamsMultiInstanceMappingTests(NamsLiveFixture fixture) => _fixture = fixture;

    [LiveNamsFact]
    public async Task TwoResolverInstancesSharingOneStore_RaceForSameIdentity_ReconcileToOneWinner()
    {
        var namsClient = _fixture.Services!.GetRequiredService<INamsClient>();
        var sharedStore = new InMemoryNamsConversationStateStore();
        var resolverA = CreateResolver(namsClient, sharedStore);
        var resolverB = CreateResolver(namsClient, sharedStore);
        var identity = UniqueIdentity();

        // Deliberately NOT awaited individually before starting the second -- both resolvers must reach
        // their own real NAMS CreateConversationAsync call independently (the only genuinely-suspending step
        // in ResolveAsync; everything before it resolves synchronously against the in-memory store) before
        // either can observe the other's write. That's what makes this a real race on the shared store
        // rather than one resolver's outer read simply finding the other's already-committed mapping.
        var taskA = resolverA.ResolveAsync(identity, CancellationToken.None);
        var taskB = resolverB.ResolveAsync(identity, CancellationToken.None);
        var results = await Task.WhenAll(taskA, taskB);

        results[0].NamsConversationId.Should().Be(results[1].NamsConversationId,
            "both resolver instances are for the same identity -- they must reconcile onto one conversation");
        results.Count(r => r.WasCreated).Should().Be(1,
            "exactly one of the two racing resolver instances should win the shared store's atomic write");
    }

    [LiveNamsFact]
    public async Task TwoResolverInstancesSharingOneStore_RaceWithDifferentUsersOnTheSameSessionSlot_LoserThrowsConflict()
    {
        var namsClient = _fixture.Services!.GetRequiredService<INamsClient>();
        var sharedStore = new InMemoryNamsConversationStateStore();
        var resolverA = CreateResolver(namsClient, sharedStore);
        var resolverB = CreateResolver(namsClient, sharedStore);

        // Same session/local-conversation slot (the store's actual key) but two DIFFERENT users -- a
        // cross-tenant collision on that slot. Per NamsConversationResolver's own doc comment, the
        // reconciliation path must still enforce the user/application check and throw rather than silently
        // handing one tenant's turn to another tenant's conversation.
        var sessionId = Guid.NewGuid().ToString("N");
        var localConversationId = Guid.NewGuid().ToString("N");
        var identityA = new NamsConversationIdentity
        {
            ApplicationId = "agent-memory-dotnet-live-tests",
            UserId = $"user-a-{Guid.NewGuid():N}",
            SessionId = sessionId,
            LocalConversationId = localConversationId
        };
        var identityB = identityA with { UserId = $"user-b-{Guid.NewGuid():N}" };

        var taskA = resolverA.ResolveAsync(identityA, CancellationToken.None);
        var taskB = resolverB.ResolveAsync(identityB, CancellationToken.None);

        // Exactly one of the two must throw NamsConversationIdentityConflictException (whichever loses the
        // shared store's atomic write finds a mapping bound to the OTHER user and must refuse to reuse it);
        // the other completes normally. Task.WhenAll surfaces only the first exception, so await each
        // individually to observe both outcomes.
        Exception? exceptionA = null, exceptionB = null;
        NamsConversationResolutionResult? resultA = null, resultB = null;
        try { resultA = await taskA; } catch (Exception ex) { exceptionA = ex; }
        try { resultB = await taskB; } catch (Exception ex) { exceptionB = ex; }

        var exceptions = new[] { exceptionA, exceptionB }.Where(e => e is not null).ToList();
        var successes = new[] { resultA, resultB }.Where(r => r is not null).ToList();

        exceptions.Should().ContainSingle(
            "exactly one of the two cross-tenant racing resolutions must be refused")
            .Which.Should().BeOfType<NamsConversationIdentityConflictException>();
        successes.Should().ContainSingle("the winning resolution completes normally for its own tenant");
    }

    private static NamsConversationResolver CreateResolver(INamsClient namsClient, InMemoryNamsConversationStateStore store) =>
        new(namsClient, store, NullLogger<NamsConversationResolver>.Instance);

    // [CallerMemberName] embeds the calling test's name in UserId -- these tests deliberately leave orphaned
    // NAMS-side conversations behind (see the type doc comment), so this aids tracing them back to the test
    // that created them, matching NamsLiveConnectivityTests.UniqueIdentity's identical convention.
    private static NamsConversationIdentity UniqueIdentity([CallerMemberName] string testName = "") => new()
    {
        ApplicationId = "agent-memory-dotnet-live-tests",
        UserId = $"test-{testName}-{Guid.NewGuid():N}",
        SessionId = Guid.NewGuid().ToString("N"),
        LocalConversationId = Guid.NewGuid().ToString("N")
    };
}
