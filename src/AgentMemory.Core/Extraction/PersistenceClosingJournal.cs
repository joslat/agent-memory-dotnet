namespace AgentMemory.Core.Extraction;

/// <summary>Tracks successful closings in the current asynchronous persistence operation.</summary>
internal static class PersistenceClosingJournal
{
    private static readonly AsyncLocal<List<(string Winner, string Loser)>?> s_closings = new();

    // Called synchronously inside PersistPreparedAsync so the value flows into its awaited work,
    // including replay, and does not flow back into the caller's post-persistence operations.
    internal static void Begin(List<(string Winner, string Loser)> closings) => s_closings.Value = closings;

    internal static void Record(string winner, string loser) => s_closings.Value?.Add((winner, loser));
}
