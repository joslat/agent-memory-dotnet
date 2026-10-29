using Neo4j.Driver;

namespace AgentMemory.Extensibility.Neo4j.Storage;

internal static class ModuleRecordTransaction
{
    internal static async Task<List<IRecord>> RowsAsync(IAsyncQueryRunner runner, string query, object parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cursor = await runner.RunAsync(query, parameters).ConfigureAwait(false);
        // Fetched batch by batch, the token checked between them, so a time budget stops a long read where it is (review 6, M2:
        // the whole result was read before the token was looked at); leaving the transaction stops the query on the server.
        var rows = new List<IRecord>();
        while (await cursor.FetchAsync().ConfigureAwait(false))
        {
            rows.Add(cursor.Current);
            if ((rows.Count & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return rows;
    }
    internal static async Task OneAsync(IAsyncQueryRunner runner, string query, object parameters, CancellationToken cancellationToken)
    {
        if ((await RowsAsync(runner, query, parameters, cancellationToken).ConfigureAwait(false)).Count != 1)
            throw new ModuleRecordRefusal("A required record or link target changed during the transaction.");
    }
    internal static string? Text(IReadOnlyDictionary<string, object> row, string key) => row.TryGetValue(key, out var value) ? value?.As<string>() : null;
}

internal sealed class ModuleRecordRefusal(string message) : Exception(message);
