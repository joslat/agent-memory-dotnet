using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentMemory.AgentFramework;

/// <summary>
/// The in-process <see cref="IBackgroundExtraction"/>: ordered per key, concurrent across keys, drained on
/// dispose. No thread is held while idle: each piece of work is a task chained after the previous one
/// with its key.
/// </summary>
internal sealed class BackgroundExtractionQueue : IBackgroundExtraction, IAsyncDisposable, IDisposable
{
    private readonly SemaphoreSlim _slots;
    private readonly TimeSpan _drainTimeout;
    private readonly ILogger<BackgroundExtractionQueue> _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, Task> _tails = new(StringComparer.Ordinal);
    private TaskCompletionSource _idle = Completed();
    private int _pending;
    private bool _closed;

    /// <summary>
    /// Where queued work gets its services: a fresh scope per item, because the turn's own scope (an ASP.NET
    /// request, say) has usually ended by the time the work runs. Null outside a container (tests).
    /// </summary>
    internal IServiceScopeFactory? Scopes { get; }

    public BackgroundExtractionQueue(int concurrency, TimeSpan drainTimeout, ILogger<BackgroundExtractionQueue> logger,
        IServiceScopeFactory? scopes = null)
    {
        Scopes = scopes;
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);
        _slots = new SemaphoreSlim(concurrency, concurrency);
        _drainTimeout = drainTimeout;
        _logger = logger;
    }

    /// <inheritdoc/>
    public int Pending => Volatile.Read(ref _pending);

    /// <inheritdoc/>
    public bool TryEnqueue(string orderingKey, Func<CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(orderingKey);
        ArgumentNullException.ThrowIfNull(work);

        lock (_gate)
        {
            if (_closed) return false;
            if (++_pending == 1) _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var previous = _tails.TryGetValue(orderingKey, out var tail) ? tail : Task.CompletedTask;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _tails[orderingKey] = done.Task;
            // On the thread pool, not the caller's context: Task.Yield would post back to a caller's
            // SynchronizationContext or TaskScheduler, and the work would start on it.
            _ = Task.Run(() => RunAsync(orderingKey, previous, done, work));
        }
        return true;
    }

    private async Task RunAsync(string key, Task previous, TaskCompletionSource done, Func<CancellationToken, Task> work)
    {
        var acquired = false;
        try
        {
            await previous.ConfigureAwait(false);
            await _slots.WaitAsync(_stop.Token).ConfigureAwait(false);
            acquired = true;
            await work(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            _logger.LogWarning("Background extraction for {Key} was cancelled at shutdown; that turn was not learned.", key);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Background extraction for {Key} failed.", key);
        }
        finally
        {
            if (acquired) _slots.Release();
            lock (_gate)
            {
                if (_tails.TryGetValue(key, out var tail) && tail == done.Task) _tails.Remove(key);
                if (--_pending == 0) _idle.TrySetResult();
            }
            done.TrySetResult();
        }
    }

    /// <inheritdoc/>
    public Task WhenIdleAsync(string orderingKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderingKey);
        Task tail;
        lock (_gate) tail = _tails.TryGetValue(orderingKey, out var t) ? t : Task.CompletedTask;
        return tail.WaitAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public Task WhenIdleAsync(CancellationToken cancellationToken = default)
    {
        Task idle;
        lock (_gate) idle = _idle.Task;
        return idle.WaitAsync(cancellationToken);
    }

    /// <summary>Stops accepting work, waits up to the drain timeout for what is queued, then cancels the rest.</summary>
    public async ValueTask DisposeAsync()
    {
        Task idle;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            idle = _idle.Task;
        }

        if (await Task.WhenAny(idle, Task.Delay(_drainTimeout)).ConfigureAwait(false) != idle)
        {
            _logger.LogWarning("Background extraction did not drain within {Seconds} s; cancelling {Pending} item(s).",
                _drainTimeout.TotalSeconds, Pending);
            await _stop.CancelAsync().ConfigureAwait(false);
            // Cancelled work finishes promptly (its token is cancelled); wait briefly so it is not cut off mid-write.
            await Task.WhenAny(idle, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The same drain for a host that disposes its container synchronously; without it that dispose
    /// throws (a singleton that is only async-disposable).
    /// </summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private static TaskCompletionSource Completed()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completed.SetResult();
        return completed;
    }
}
