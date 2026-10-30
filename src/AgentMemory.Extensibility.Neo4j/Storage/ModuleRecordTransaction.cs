using Neo4j.Driver;

namespace AgentMemory.Extensibility.Neo4j.Storage;

internal static class ModuleRecordTransaction
{
    internal static async Task<List<IRecord>> RowsAsync(IAsyncQueryRunner runner, string query, object parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cursor = await runner.RunAsync(query, parameters).ConfigureAwait(false);
        // The token checked on every row (review 6, M2, and review 7, W3: every 1,024 rows); a statement still working before its
        // first row is stopped by the deadline the server holds, and leaving the transaction stops it there too.
        var rows = new List<IRecord>();
        while (await cursor.FetchAsync().ConfigureAwait(false))
        {
            rows.Add(cursor.Current);
            cancellationToken.ThrowIfCancellationRequested();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return rows;
    }
    // Each row as it arrives, the token checked on every one, none kept here (review 7, W6: a read held every candidate row,
    // values and all, before it ranked them).
    internal static async Task EachAsync(IAsyncQueryRunner runner, string query, object parameters, Action<IRecord> each, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cursor = await runner.RunAsync(query, parameters).ConfigureAwait(false);
        while (await cursor.FetchAsync().ConfigureAwait(false))
        {
            each(cursor.Current);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
    internal static async Task OneAsync(IAsyncQueryRunner runner, string query, object parameters, CancellationToken cancellationToken)
    {
        if ((await RowsAsync(runner, query, parameters, cancellationToken).ConfigureAwait(false)).Count != 1)
            throw new ModuleRecordRefusal("A required record or link target changed during the transaction.");
    }
    internal static string? Text(IReadOnlyDictionary<string, object> row, string key) => row.TryGetValue(key, out var value) ? value?.As<string>() : null;
}

internal sealed class ModuleRecordRefusal(string message) : Exception(message);
