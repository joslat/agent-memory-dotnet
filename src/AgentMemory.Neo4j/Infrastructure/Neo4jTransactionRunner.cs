using System.Diagnostics;
using Microsoft.Extensions.Logging;
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
        var payload = activity is null ? null : new PayloadAccumulator();
        var transactionEntryStartedAt = activity is null ? 0 : Stopwatch.GetTimestamp();

        var session = _sessionFactory.OpenSession(AccessMode.Read);
        await using var _ = session.ConfigureAwait(false); // ConfigureAwait the disposal without rebinding session's type
        try
        {
            return await session.ExecuteReadAsync(
                Instrument(work, "read", activity, payload, transactionEntryStartedAt)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
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
        using var activity = AgentMemoryDiagnostics.Source.StartActivity("memory.db.tx", ActivityKind.Client);
        activity?.SetTag("db.mode", "write");
        var payload = activity is null ? null : new PayloadAccumulator();
        var transactionEntryStartedAt = activity is null ? 0 : Stopwatch.GetTimestamp();

        var session = _sessionFactory.OpenSession(AccessMode.Write);
        await using var _ = session.ConfigureAwait(false); // ConfigureAwait the disposal without rebinding session's type
        try
        {
            return await session.ExecuteWriteAsync(
                Instrument(work, "write", activity, payload, transactionEntryStartedAt)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            _logger.LogError(ex, "Error executing write transaction.");
            throw;
        }
        finally
        {
            TagPayload(activity, payload);
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
            : runner =>
            {
                // The driver's public API exposes acquisition counts and a timeout, but not wait duration.
                // This upper-bound estimate starts immediately before ExecuteRead/WriteAsync and stops when
                // its transaction callback begins. It therefore includes connection acquisition, routing,
                // and transaction begin; the `_est` suffix is permanent and prevents a pure-pool-wait claim.
                transaction.SetTag(
                    "db.transaction_entry_ms_est",
                    Stopwatch.GetElapsedTime(transactionEntryStartedAt).TotalMilliseconds);
                return work(new CountingQueryRunner(runner, mode, transaction, payload!));
            };

    private static void TagPayload(Activity? activity, PayloadAccumulator? payload)
    {

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
            using var activity = AgentMemoryDiagnostics.Source.StartActivity(
                "memory.db.query", ActivityKind.Client, _parent);
            activity?.SetTag("db.mode", _mode);
            activity?.SetTag("db.query.fingerprint", CypherQueryRegistry.FingerprintFor(queryText));
            var cursor = await run().ConfigureAwait(false);
            return new CountingResultCursor(cursor, _payload);
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

        public CountingResultCursor(IResultCursor inner, PayloadAccumulator payload)
        {
            _inner = inner;
            _payload = payload;
        }

        public IRecord Current => _inner.Current;

        public bool IsOpen => _inner.IsOpen;

        public Task<string[]> KeysAsync() => _inner.KeysAsync();

        public Task<IResultSummary> ConsumeAsync() => _inner.ConsumeAsync();

        public Task<IRecord> PeekAsync() => _inner.PeekAsync();

        public async Task<bool> FetchAsync()
        {
            var fetched = await _inner.FetchAsync().ConfigureAwait(false);
            if (fetched)
                _payload.Add(_inner.Current);
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
                    yield return record;
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class PayloadAccumulator
    {
        private long _recordCount;
        private long _bytesEstimate;

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
