using System.Runtime.CompilerServices;
using AgentMemory.Nams.Client;

namespace AgentMemory.Tests.Integration.Nams;

/// <summary>
/// Shared helpers for live-NAMS tests across this folder's several test classes.
/// </summary>
internal static class NamsLiveTestHelpers
{
    /// <summary>
    /// Bounded poll helper -- never waits longer than <paramref name="timeout"/>. Swallows exceptions from
    /// <paramref name="condition"/> itself (a live HTTP call can hit a transient blip mid-poll) and keeps
    /// retrying within the remaining budget, matching <c>Neo4jIntegrationFixture.WaitForVectorIndexesAsync</c>'s
    /// established pattern for this repo's other eventual-consistency polls.
    /// </summary>
    public static async Task<bool> PollUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
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

    /// <summary>
    /// Generates a <c>test-{testName}-{guid}</c> user id, embedding the calling test's name via
    /// <see cref="CallerMemberNameAttribute"/> for tracing orphaned conversations these tests create but never
    /// delete. Consolidates what had drifted into 5 near-identical private copies across
    /// <c>NamsLiveConnectivityTests</c>/<c>NamsMultiInstanceMappingTests</c>/<c>NamsSessionRestoreTests</c>/
    /// <c>NamsConversationLifecycleTests</c>/<c>NamsReasoningTests</c> (a Phase 10-review finding).
    /// </summary>
    public static string UniqueUserId([CallerMemberName] string testName = "") =>
        $"test-{testName}-{Guid.NewGuid():N}";

    /// <summary>
    /// Fetches any one entity already present in the shared live dev workspace, for tests that need a real
    /// entity id to operate on but don't care which one (entity creation only happens via the async extraction
    /// pipeline or the out-of-scope <c>POST /entities</c> endpoint -- see the Phase 10g planning doc). Asserts
    /// non-empty first so a caller's failure points at "workspace has no entities" rather than a confusing
    /// downstream null/index exception. Consolidates 4 near-identical inline copies across
    /// <c>NamsEntityGraphTests</c>/<c>NamsReasoningTests</c>/<c>NamsCypherQueryTests</c> (a Phase 10-review
    /// finding).
    /// </summary>
    public static async Task<string> GetAnyExistingEntityIdAsync(INamsClient namsClient, int limit, CancellationToken cancellationToken)
    {
        var entities = await namsClient.ListEntitiesAsync(limit, cancellationToken);
        if (entities.Count == 0)
            throw new InvalidOperationException(
                "The shared live dev workspace has no entities -- expected at least one from earlier phases' " +
                "extraction runs. If the workspace was reset, this test needs a different entity source.");
        return entities[0].Id;
    }
}
