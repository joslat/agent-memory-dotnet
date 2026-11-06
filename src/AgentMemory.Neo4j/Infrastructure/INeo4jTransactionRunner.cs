using Neo4j.Driver;

namespace AgentMemory.Neo4j.Infrastructure;

public interface INeo4jTransactionRunner
{
    Task<T> ReadAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default);

    /// <summary>
    /// A read the server stops after <paramref name="timeout"/>, whatever deadline is configured: a statement in progress stops where
    /// it is, which a cancellation token cannot do, since the driver takes none. A runner that cannot set one runs the plain read,
    /// and the caller's token is then looked at between rows only.
    /// </summary>
    Task<T> ReadAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        ReadAsync(work, cancellationToken);
    Task ReadAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default);
    Task<T> WriteAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default);
    Task WriteAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default);
}
