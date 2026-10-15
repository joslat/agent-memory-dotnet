using AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;

namespace Commitments.NativeChecks;

// Pure observation around the real transaction runner. Atomic coordination is forwarded too;
// instrumenting calls must not silently replace the database's rollback capability.
internal sealed class ObservedTransactions : INeo4jTransactionRunner, INeo4jAtomicTransactionRunner
{
    private readonly INeo4jTransactionRunner _inner;
    private readonly INeo4jAtomicTransactionRunner _atomic;
    private readonly Action<string> _observe;
    public ObservedTransactions(INeo4jTransactionRunner inner, Action<string> observe)
    {
        _inner = inner;
        _atomic = inner as INeo4jAtomicTransactionRunner
            ?? throw new InvalidOperationException("Native fixture requires actual atomic Neo4j transactions.");
        _observe = observe;
    }
    public Task<T> ReadAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default)
        => _inner.ReadAsync(r => work(new ObservedQueryRunner(r, _observe)), cancellationToken);
    public Task ReadAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default)
        => _inner.ReadAsync(r => work(new ObservedQueryRunner(r, _observe)), cancellationToken);
    public Task<T> WriteAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default)
        => _inner.WriteAsync(r => work(new ObservedQueryRunner(r, _observe)), cancellationToken);
    public Task WriteAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default)
        => _inner.WriteAsync(r => work(new ObservedQueryRunner(r, _observe)), cancellationToken);
    public Task<T> ExecuteAtomicWriteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
        => _atomic.ExecuteAtomicWriteAsync(work, cancellationToken);

    private sealed class ObservedQueryRunner(IAsyncQueryRunner inner, Action<string> observe) : IAsyncQueryRunner
    {
        public void Dispose() => inner.Dispose();
        public ValueTask DisposeAsync() => inner.DisposeAsync();
        public Task<IResultCursor> RunAsync(string query) { observe(query); return inner.RunAsync(query); }
        public Task<IResultCursor> RunAsync(string query, object parameters) { observe(query); return inner.RunAsync(query, parameters); }
        public Task<IResultCursor> RunAsync(string query, IDictionary<string, object> parameters) { observe(query); return inner.RunAsync(query, parameters); }
        public Task<IResultCursor> RunAsync(Query query) { observe(query.Text); return inner.RunAsync(query); }
    }
}
