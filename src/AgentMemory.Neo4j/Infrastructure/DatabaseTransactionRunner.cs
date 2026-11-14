using Neo4j.Driver;

namespace AgentMemory.Neo4j.Infrastructure;

/// <summary>
/// REV5-3: a transaction runner bound to one named database, so the migration runner can apply the schema scripts to a
/// database the store provisions per application, as schema bootstrap applies them to the default one.
/// </summary>
internal sealed class DatabaseTransactionRunner(INeo4jSessionFactory sessions, string database) : INeo4jTransactionRunner
{
    public async Task<T> ReadAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = sessions.OpenSession(database, AccessMode.Read);
        await using var _ = session.ConfigureAwait(false);
        return await session.ExecuteReadAsync(work).ConfigureAwait(false);
    }

    public Task ReadAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default) =>
        ReadAsync(async runner => { await work(runner).ConfigureAwait(false); return true; }, cancellationToken);

    public async Task<T> WriteAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = sessions.OpenSession(database, AccessMode.Write);
        await using var _ = session.ConfigureAwait(false);
        return await session.ExecuteWriteAsync(work).ConfigureAwait(false);
    }

    public Task WriteAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default) =>
        WriteAsync(async runner => { await work(runner).ConfigureAwait(false); return true; }, cancellationToken);
}
