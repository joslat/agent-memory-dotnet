using System.Diagnostics;
using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Diagnostics;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Infrastructure;

/// <summary>
/// Executes managed read/write transactions against Neo4j.
/// </summary>
/// <remarks>
/// <para>
/// The Neo4j .NET driver does not accept a <see cref="CancellationToken"/> on its
/// <c>ExecuteReadAsync</c>/<c>ExecuteWriteAsync</c>/<c>RunAsync</c> APIs, so a query that is
/// already in-flight cannot be interrupted. To still honor cooperative cancellation, every entry
/// point throws <see cref="OperationCanceledException"/> if the token is already cancelled
/// <em>before</em> a session is opened or work is started.
/// </para>
/// <para>
/// <b>Instrumentation.</b> Each entry point opens a <c>memory.db.tx</c> span, and the
/// <see cref="IAsyncQueryRunner"/> handed to the caller's work delegate is wrapped so every
/// <c>RunAsync</c> becomes a nested <c>memory.db.query</c> span. Counting here — inside the product,
/// on the path every consumer actually executes — rather than in a benchmark-only decorator is
/// deliberate: it means a measurement harness observes exactly the object graph users run. When no
/// listener is attached, <c>StartActivity</c> returns null and the wrapper is not even allocated, so
/// the cost is one <see cref="ActivitySource.HasListeners"/> check per transaction.
/// </para>
/// </remarks>
internal sealed class Neo4jTransactionRunner : INeo4jTransactionRunner
{
    private readonly INeo4jSessionFactory _sessionFactory;
    private readonly ILogger<Neo4jTransactionRunner> _logger;

    public Neo4jTransactionRunner(INeo4jSessionFactory sessionFactory, ILogger<Neo4jTransactionRunner> logger)
    {
        _sessionFactory = sessionFactory;
        _logger = logger;
    }

    public async Task<T> ReadAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var activity = AgentMemoryDiagnostics.Source.StartActivity("memory.db.tx", ActivityKind.Client);
        activity?.SetTag("db.mode", "read");

        var session = _sessionFactory.OpenSession(AccessMode.Read);
        await using var _ = session.ConfigureAwait(false); // ConfigureAwait the disposal without rebinding session's type
        try
        {
            return await session.ExecuteReadAsync(Instrument(work, "read", activity)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            _logger.LogError(ex, "Error executing read transaction.");
            throw;
        }
    }

    public async Task ReadAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default)
    {
        await ReadAsync(async tx =>
        {
            await work(tx).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<T> WriteAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var activity = AgentMemoryDiagnostics.Source.StartActivity("memory.db.tx", ActivityKind.Client);
        activity?.SetTag("db.mode", "write");

        var session = _sessionFactory.OpenSession(AccessMode.Write);
        await using var _ = session.ConfigureAwait(false); // ConfigureAwait the disposal without rebinding session's type
        try
        {
            return await session.ExecuteWriteAsync(Instrument(work, "write", activity)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            _logger.LogError(ex, "Error executing write transaction.");
            throw;
        }
    }

    public async Task WriteAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default)
    {
        await WriteAsync(async tx =>
        {
            await work(tx).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the caller's work delegate unchanged when nothing is listening (the overwhelmingly common
    /// case — zero allocation, zero indirection), or a delegate that hands the work a counting wrapper
    /// when a listener is attached. <paramref name="transaction"/> is captured so nested query spans are
    /// parented to their transaction even though the driver may resume the delegate on another thread.
    /// </summary>
    private static Func<IAsyncQueryRunner, Task<T>> Instrument<T>(
        Func<IAsyncQueryRunner, Task<T>> work, string mode, Activity? transaction) =>
        transaction is null ? work : runner => work(new CountingQueryRunner(runner, mode, transaction));

    /// <summary>
    /// Wraps the driver's query runner so each <c>RunAsync</c> emits a <c>memory.db.query</c> span. Pure
    /// pass-through otherwise: every overload forwards to the inner runner and returns its cursor
    /// untouched, so result streaming, error behaviour, and the managed-transaction retry contract are
    /// unaffected. Only ever constructed when a listener is attached.
    /// </summary>
    private sealed class CountingQueryRunner : IAsyncQueryRunner
    {
        private readonly IAsyncQueryRunner _inner;
        private readonly string _mode;
        private readonly ActivityContext _parent;

        public CountingQueryRunner(IAsyncQueryRunner inner, string mode, Activity transaction)
        {
            _inner = inner;
            _mode = mode;
            _parent = transaction.Context;
        }

        public Task<IResultCursor> RunAsync(string query) => TrackAsync(() => _inner.RunAsync(query));

        public Task<IResultCursor> RunAsync(string query, object parameters) =>
            TrackAsync(() => _inner.RunAsync(query, parameters));

        public Task<IResultCursor> RunAsync(string query, IDictionary<string, object> parameters) =>
            TrackAsync(() => _inner.RunAsync(query, parameters));

        public Task<IResultCursor> RunAsync(Query query) => TrackAsync(() => _inner.RunAsync(query));

        // The span covers dispatching the query, NOT consuming its results: the driver returns a cursor
        // that the caller streams afterwards, so an enclosing span here would end long before the records
        // are actually read. Record counting and payload-size estimation therefore require wrapping the
        // cursor itself, which is deliberately out of scope for the first measurement increment — the
        // headline number this instrumentation exists to produce is round trips, and that is exact.
        private async Task<IResultCursor> TrackAsync(Func<Task<IResultCursor>> run)
        {
            using var activity = AgentMemoryDiagnostics.Source.StartActivity(
                "memory.db.query", ActivityKind.Client, _parent);
            activity?.SetTag("db.mode", _mode);
            return await run().ConfigureAwait(false);
        }

        // Forwarded, not swallowed. The wrapper must be indistinguishable from the runner it replaces:
        // if anything ever disposes the runner it was handed, the effect has to be identical with and
        // without instrumentation. (Nothing in this codebase does — the driver owns the transaction's
        // lifetime — but a no-op here would be a behavioural difference that only appears when a
        // listener is attached, which is the exact class of bug this design exists to avoid.)
        public void Dispose() => _inner.Dispose();

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
