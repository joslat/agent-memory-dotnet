using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Neo4j.Queries;
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
internal sealed class Neo4jTransactionRunner : INeo4jTransactionRunner, INeo4jAtomicTransactionRunner
{
    private readonly INeo4jSessionFactory _sessionFactory;
    private readonly ILogger<Neo4jTransactionRunner> _logger;

    private readonly AsyncLocal<IAsyncQueryRunner?> _ambientWriteTransaction = new();

    /// <summary>
    /// Applies the configured server-side deadline, or null when none is configured.
    /// </summary>
    /// <remarks>
    /// Built once. Null is passed straight through to the driver's overload that takes no transaction
    /// config, so an unconfigured deployment emits byte-identical driver calls to before this existed.
    /// </remarks>
    private readonly Action<TransactionConfigBuilder>? _transactionConfig;

    public Neo4jTransactionRunner(
        INeo4jSessionFactory sessionFactory,
        ILogger<Neo4jTransactionRunner> logger,
        IOptions<Neo4jOptions>? options = null)
    {
        _sessionFactory = sessionFactory;
        _logger = logger;

        var timeout = options?.Value.TransactionTimeout;
        _transactionConfig = timeout is { } value && value > TimeSpan.Zero
            ? builder => builder.WithTimeout(value)
            : null;
    }

    public async Task<T> ReadAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_ambientWriteTransaction.Value is { } ambient)
            return await work(ambient).ConfigureAwait(false);

        using var activity = AgentMemoryDiagnostics.Source.StartActivity("memory.db.tx", ActivityKind.Client);
        activity?.SetTag("db.system", "neo4j");
        activity?.SetTag("db.mode", "read");
        var payload = activity is null ? null : new PayloadAccumulator();
        var transactionEntryStartedAt = activity is null ? 0 : Stopwatch.GetTimestamp();

        var session = _sessionFactory.OpenSession(AccessMode.Read);
        await using var _ = session.ConfigureAwait(false); // ConfigureAwait the disposal without rebinding session's type
        try
        {
            // Always the config-taking overload: the driver's single-argument form forwards to this one
            // with a null config, so passing null here is byte-identical to the call this used to make.
            return await session.ExecuteReadAsync(
                Instrument(work, "read", activity, payload, transactionEntryStartedAt),
                _transactionConfig).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            MemoryTelemetry.RecordException(activity, ex);
            _logger.LogError(ex, "Error executing read transaction.");
            throw;
        }
        finally
        {
            TagPayload(activity, payload);
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
        if (_ambientWriteTransaction.Value is { } ambient)
            return await work(ambient).ConfigureAwait(false);

        using var activity = AgentMemoryDiagnostics.Source.StartActivity("memory.db.tx", ActivityKind.Client);
        activity?.SetTag("db.system", "neo4j");
        activity?.SetTag("db.mode", "write");
        var payload = activity is null ? null : new PayloadAccumulator();
        var transactionEntryStartedAt = activity is null ? 0 : Stopwatch.GetTimestamp();

        var session = _sessionFactory.OpenSession(AccessMode.Write);
        await using var _ = session.ConfigureAwait(false); // ConfigureAwait the disposal without rebinding session's type
        try
        {
            return await session.ExecuteWriteAsync(
                Instrument(work, "write", activity, payload, transactionEntryStartedAt),
                _transactionConfig).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            MemoryTelemetry.RecordException(activity, ex);
            _logger.LogError(ex, "Error executing write transaction.");
            throw;
        }
        finally
        {
            TagPayload(activity, payload);
        }
    }

    public async Task<T> ExecuteAtomicWriteAsync<T>(
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Nested logical units join the outer unit. This also keeps the transaction boundary
        // well-defined when a higher-level persistence workflow composes another one.
        if (_ambientWriteTransaction.Value is not null)
            return await work(cancellationToken).ConfigureAwait(false);

        using var activity = AgentMemoryDiagnostics.Source.StartActivity("memory.db.tx", ActivityKind.Client);
        activity?.SetTag("db.system", "neo4j");
        activity?.SetTag("db.mode", "write");
        activity?.SetTag("db.transaction.logical_unit", true);
        var payload = activity is null ? null : new PayloadAccumulator();
        var transactionEntryStartedAt = activity is null ? 0 : Stopwatch.GetTimestamp();

        var session = _sessionFactory.OpenSession(AccessMode.Write);
        await using var _ = session.ConfigureAwait(false);
        IAsyncTransaction? transaction = null;
        try
        {
            // Explicit rather than managed transaction: the callback mutates in-memory outcome state
            // and must execute exactly once. The caller owns any whole-operation retry after rollback.
            // The same deadline as the managed paths. Covering two of the three transaction entry
            // points would be worse than covering none: the uncovered one is the FUSED PERSISTENCE
            // path, which is the longest-running write in the system, and an operator who configured
            // a timeout would reasonably believe it applied everywhere.
            transaction = await session.BeginTransactionAsync(_transactionConfig).ConfigureAwait(false);
            activity?.SetTag(
                "db.transaction_entry_ms_est",
                Stopwatch.GetElapsedTime(transactionEntryStartedAt).TotalMilliseconds);

            IAsyncQueryRunner ambientRunner = activity is null
                ? transaction
                : new CountingQueryRunner(transaction, "write", activity, payload!);
            _ambientWriteTransaction.Value = ambientRunner;

            var result = await work(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync().ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            MemoryTelemetry.RecordException(activity, ex);
            Exception? rollbackFailure = null;
            if (transaction is not null)
            {
                try
                {
                    await transaction.RollbackAsync().ConfigureAwait(false);
                }
                catch (Exception rollbackException)
                {
                    rollbackFailure = rollbackException;
                    _logger.LogWarning(rollbackException, "Failed to roll back atomic memory transaction.");
                }
            }

            if (rollbackFailure is not null)
                throw new AggregateException(
                    "Atomic memory transaction failed and rollback could not be confirmed.", ex, rollbackFailure);

            _logger.LogError(ex, "Error executing atomic memory transaction.");
            throw;
        }
        finally
        {
            _ambientWriteTransaction.Value = null;
            // Query spans are closed before the transaction is disposed, so a failing dispose cannot
            // leave them open.
            TagPayload(activity, payload);
            if (transaction is not null)
                await transaction.DisposeAsync().ConfigureAwait(false);
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
        Func<IAsyncQueryRunner, Task<T>> work,
        string mode,
        Activity? transaction,
        PayloadAccumulator? payload,
        long transactionEntryStartedAt) =>
        transaction is null
            ? work
            : Attempted(transaction, payload!, runner =>
            {
                // The driver's public API exposes acquisition counts and a timeout, but not wait duration.
                // This upper-bound estimate starts immediately before ExecuteRead/WriteAsync and stops when
                // its transaction callback begins. It therefore includes connection acquisition, routing,
                // and transaction begin; the `_est` suffix is permanent and prevents a pure-pool-wait claim.
                transaction.SetTag(
                    "db.transaction_entry_ms_est",
                    Stopwatch.GetElapsedTime(transactionEntryStartedAt).TotalMilliseconds);
                return work(new CountingQueryRunner(runner, mode, transaction, payload!));
            });

    /// <summary>
    /// Counts how many times the driver invoked the transaction callback: a managed transaction retries
    /// transient failures by calling it again, invisibly. Tagged as <c>db.attempts</c> and
    /// <c>db.retries</c>, with a <c>db.attempt</c> event per call.
    /// </summary>
    private static Func<IAsyncQueryRunner, Task<T>> Attempted<T>(
        Activity transaction, PayloadAccumulator payload, Func<IAsyncQueryRunner, Task<T>> work)
    {
        int attempts = 0;
        return runner =>
        {
            var n = Interlocked.Increment(ref attempts);
            transaction.SetTag("db.attempts", n);
            transaction.SetTag("db.retries", n - 1);
            if (n > 1)
            {
                // The failed attempt's unread results died with it: their spans end here, not after the retry.
                payload.CloseOpenQueries();
                transaction.AddEvent(new ActivityEvent("db.attempt", tags: new ActivityTagsCollection { ["db.attempt"] = n }));
            }
            return work(runner);
        };
    }

    private static void TagPayload(Activity? activity, PayloadAccumulator? payload)
    {
        payload?.CloseOpenQueries();

        if (activity is null || payload is null) return;
        activity.SetTag("db.records", payload.RecordCount);
        activity.SetTag("db.bytes_est", payload.BytesEstimate);
    }

    /// <summary>
    /// Wraps the driver's query runner so each <c>RunAsync</c> emits a <c>memory.db.query</c> span and
    /// returns a cursor that counts records as callers materialize them. Only ever constructed when a
    /// listener is attached.
    /// </summary>
    private sealed class CountingQueryRunner : IAsyncQueryRunner
    {
        private readonly IAsyncQueryRunner _inner;
        private readonly string _mode;
        private readonly ActivityContext _parent;
        private readonly PayloadAccumulator _payload;

        public CountingQueryRunner(
            IAsyncQueryRunner inner,
            string mode,
            Activity transaction,
            PayloadAccumulator payload)
        {
            _inner = inner;
            _mode = mode;
            _parent = transaction.Context;
            _payload = payload;
            payload.Track(this);
        }

        public Task<IResultCursor> RunAsync(string query) =>
            TrackAsync(query, () => _inner.RunAsync(query));

        public Task<IResultCursor> RunAsync(string query, object parameters) =>
            TrackAsync(query, () => _inner.RunAsync(query, parameters));

        public Task<IResultCursor> RunAsync(string query, IDictionary<string, object> parameters) =>
            TrackAsync(query, () => _inner.RunAsync(query, parameters));

        public Task<IResultCursor> RunAsync(Query query) =>
            TrackAsync(query.Text, () => _inner.RunAsync(query));

        private async Task<IResultCursor> TrackAsync(
            string queryText,
            Func<Task<IResultCursor>> run)
        {
            // Not `using`: the span ends when the result has been READ (consumed or exhausted), not when
            // the driver returned a cursor, which was before the server had streamed anything (W14).
            // A new query on this runner means the previous results were buffered by the driver: their
            // spans end now, not at commit (a result nobody reads would otherwise span the whole unit).
            CloseOpenQueries();
            var activity = AgentMemoryDiagnostics.Source.StartActivity(
                "memory.db.query", ActivityKind.Client, _parent);
            var fingerprint = activity is null ? null : CypherQueryRegistry.FingerprintFor(queryText);
            activity?.SetTag("db.system", "neo4j");
            activity?.SetTag("db.mode", _mode);
            activity?.SetTag("db.query.fingerprint", fingerprint);
            activity?.SetTag("db.operation.name", fingerprint);
            IResultCursor cursor;
            try
            {
                cursor = await run().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                MemoryTelemetry.RecordException(activity, ex);
                activity?.Dispose();
                throw;
            }
            var counting = new CountingResultCursor(cursor, _payload, activity);
            if (activity is not null) _open.Enqueue(counting);
            return counting;
        }

        // Query spans still open when the transaction ends (a caller that neither consumed nor read to
        // the end) are closed with it, so no span outlives its transaction.
        private readonly System.Collections.Concurrent.ConcurrentQueue<CountingResultCursor> _open = new();

        internal void CloseOpenQueries()
        {
            while (_open.TryDequeue(out var cursor)) cursor.Abandon();
        }

        // Forwarded, not swallowed. The wrapper must be indistinguishable from the runner it replaces:
        // if anything ever disposes the runner it was handed, the effect has to be identical with and
        // without instrumentation. (Nothing in this codebase does — the driver owns the transaction's
        // lifetime — but a no-op here would be a behavioural difference that only appears when a
        // listener is attached, which is the exact class of bug this design exists to avoid.)
        public void Dispose() => _inner.Dispose();

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    /// <summary>
    /// Counts only records exposed by <see cref="IResultCursor.FetchAsync"/>. This is deliberately the
    /// materialized payload, not an invented wire-byte count: the driver does not expose Bolt bytes, and
    /// <see cref="IResultCursor.ConsumeAsync"/> can discard unread rows without exposing their values.
    /// </summary>
    private sealed class CountingResultCursor : IResultCursor
    {
        private readonly IResultCursor _inner;
        private readonly PayloadAccumulator _payload;
        private readonly Activity? _query;
        private long _rows;

        public CountingResultCursor(IResultCursor inner, PayloadAccumulator payload, Activity? query)
        {
            _inner = inner;
            _payload = payload;
            _query = query;
        }

        public IRecord Current => _inner.Current;

        public bool IsOpen => _inner.IsOpen;

        public Task<string[]> KeysAsync() => _inner.KeysAsync();

        public async Task<IResultSummary> ConsumeAsync()
        {
            IResultSummary summary;
            try
            {
                summary = await _inner.ConsumeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                MemoryTelemetry.RecordException(_query, ex);
                End();
                throw;
            }
            // The server's own clock: time until the first record was available, and until the result was
            // consumed. Read from the summary the caller asked for -- never an extra call to get it.
            if (_query is not null && !_query.IsStopped)
            {
                _query.SetTag("db.server.available_ms", summary.ResultAvailableAfter.TotalMilliseconds);
                _query.SetTag("db.server.consumed_ms", summary.ResultConsumedAfter.TotalMilliseconds);
            }
            End();
            return summary;
        }

        public Task<IRecord> PeekAsync() => _inner.PeekAsync();

        public async Task<bool> FetchAsync()
        {
            bool fetched;
            try
            {
                fetched = await _inner.FetchAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                MemoryTelemetry.RecordException(_query, ex);
                End();
                throw;
            }
            if (fetched)
            {
                _payload.Add(_inner.Current);
                _rows++;
            }
            else
            {
                End();
            }
            return fetched;
        }

        public async IAsyncEnumerator<IRecord> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            var enumerator = _inner.GetAsyncEnumerator(cancellationToken);
            try
            {
                while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    var record = enumerator.Current;
                    _payload.Add(record);
                    _rows++;
                    yield return record;
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
                End();
            }
        }

        private void End()
        {
            if (_query is null || _query.IsStopped) return;
            _query.SetTag("db.rows", _rows);
            _query.Dispose();
        }

        /// <summary>
        /// Ends the span of a result its caller stopped reading: <c>db.result.read</c> is <c>none</c> when
        /// no record was read, <c>partial</c> when some were (a single fetch, a cancelled read).
        /// </summary>
        internal void Abandon()
        {
            if (_query is null || _query.IsStopped) return;
            _query.SetTag("db.result.read", Interlocked.Read(ref _rows) > 0 ? "partial" : "none");
            End();
        }
    }

    private sealed class PayloadAccumulator
    {
        private long _recordCount;
        private long _bytesEstimate;
        private readonly System.Collections.Concurrent.ConcurrentQueue<CountingQueryRunner> _runners = new();

        public void Track(CountingQueryRunner runner) => _runners.Enqueue(runner);

        public void CloseOpenQueries()
        {
            foreach (var runner in _runners) runner.CloseOpenQueries();
        }

        public long RecordCount => Interlocked.Read(ref _recordCount);

        public long BytesEstimate => Interlocked.Read(ref _bytesEstimate);

        public void Add(IRecord record)
        {
            Interlocked.Increment(ref _recordCount);
            Interlocked.Add(ref _bytesEstimate, EstimateMap(record.Values));
        }

        private static long EstimateValue(object? value) => value switch
        {
            null => 0,
            string text => 2L * text.Length,
            bool => 1,
            char => 2,
            byte or sbyte or short or ushort or int or uint or long or ulong => 8,
            float or double => 8,
            decimal => 16,
            IEntity entity => EstimateMap(entity.Properties),
            IReadOnlyDictionary<string, object> map => EstimateMap(map),
            IDictionary<string, object> map => EstimateValues(map.Values),
            System.Collections.IDictionary map => EstimateDictionary(map),
            System.Collections.IEnumerable sequence => EstimateSequence(sequence),
            _ => 8,
        };

        private static long EstimateMap(IReadOnlyDictionary<string, object> map) =>
            EstimateValues(map.Values);

        private static long EstimateValues(IEnumerable<object> values) =>
            values.Sum(EstimateValue);

        private static long EstimateDictionary(System.Collections.IDictionary map)
        {
            long bytes = 0;
            foreach (System.Collections.DictionaryEntry entry in map)
                bytes += EstimateValue(entry.Value);
            return bytes;
        }

        private static long EstimateSequence(System.Collections.IEnumerable sequence)
        {
            long bytes = 0;
            foreach (var item in sequence)
                bytes += EstimateValue(item);
            return bytes;
        }
    }
}
