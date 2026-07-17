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
}
