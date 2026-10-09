namespace AgentMemory.RouterArena.Record;

/// <summary>
/// A run file rewritten after every turn. Another process (a scanner, an indexer, an editor) can hold it open for a moment:
/// world 9's run 2 stopped at turn 51 of 252 on "a user-mapped section open" (2026-10-10). The write is tried again a few
/// times before the run fails.
/// </summary>
internal static class RunFile
{
    public static async Task WriteAsync(string path, string contents, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await File.WriteAllTextAsync(path, contents, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
