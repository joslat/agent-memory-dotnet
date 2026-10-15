using Neo4j.Driver;

namespace AgentMemory.Extensibility.Neo4j.Storage;

internal static class ModuleRecordTransaction
{
    internal static async Task<List<IRecord>> RowsAsync(IAsyncQueryRunner runner, string query, object parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cursor = await runner.RunAsync(query, parameters).ConfigureAwait(false);
        var rows = await cursor.ToListAsync().ConfigureAwait(false);
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
