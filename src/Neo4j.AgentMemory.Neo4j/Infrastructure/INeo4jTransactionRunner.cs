using Neo4j.Driver;

namespace Neo4j.AgentMemory.Neo4j.Infrastructure;

public interface INeo4jTransactionRunner
{
    Task<T> ReadAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default);
    Task ReadAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default);
    Task<T> WriteAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default);
    Task WriteAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default);
}
