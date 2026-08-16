using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain.Enrichment;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Core.Enrichment;

/// <summary>
/// Represents a single queued enrichment work item.
/// </summary>
internal record EnrichmentItem(string EntityId, int RetryCount = 0);

/// <summary>
/// Non-blocking background queue that runs enrichment providers asynchronously.
/// Uses <see cref="System.Threading.Channels.Channel{T}"/> with DropOldest overflow,
/// a fixed pool of worker tasks (no IHostedService), and configurable retry logic.
/// </summary>
internal sealed class BackgroundEnrichmentQueue : IBackgroundEnrichmentQueue, IDisposable, IAsyncDisposable
{
    private readonly Channel<EnrichmentItem> _channel;
    private readonly IReadOnlyList<IEnrichmentService> _enrichmentServices;
    private readonly IEntityRepository _entityRepository;
    private readonly EnrichmentQueueOptions _options;
    private readonly ILogger<BackgroundEnrichmentQueue> _logger;
    private readonly Task _processingTask;
    private readonly CancellationTokenSource _cts = new();
    private int _activeCount;
    private bool _disposed;
    private long _dropped;
    private long _abandonedOnShutdown;

    /// <inheritdoc/>
    public int QueueDepth => _options.Enabled ? _channel.Reader.Count : 0;

    /// <inheritdoc/>
    public bool IsProcessing => _activeCount > 0;

    /// <summary>
    /// Initializes a new instance of the <see cref="BackgroundEnrichmentQueue"/> class.
    /// </summary>
    public BackgroundEnrichmentQueue(
        IEnumerable<IEnrichmentService> enrichmentServices,
        IEntityRepository entityRepository,
        IOptions<EnrichmentQueueOptions> options,
        ILogger<BackgroundEnrichmentQueue> logger)
    {
        _enrichmentServices = enrichmentServices.ToList().AsReadOnly();
        _entityRepository = entityRepository;
        _options = options.Value;
        _logger = logger;

        var channelOptions = new BoundedChannelOptions(_options.MaxQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false
        };

        // The itemDropped callback, for the same reason MemoryAccessTrackingChannel needs one: under
        // DropOldest, TryWrite returns TRUE and silently discards the oldest queued item. A counter
        // keyed on the return value therefore reads zero forever while the queue throws work away, and
        // an operator whose entities stopped being enriched has nothing at all to look at. This is the
        // identical defect already found and fixed on the access-tracking channel; it was still here.
        _channel = Channel.CreateBounded(channelOptions, (EnrichmentItem dropped) => OnDropped(dropped));

        _processingTask = _options.Enabled
            ? StartWorkersAsync(_cts.Token)
            : Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task EnqueueAsync(string entityId, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || _disposed) return Task.CompletedTask;
        _channel.Writer.TryWrite(new EnrichmentItem(entityId));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task EnqueueBatchAsync(IEnumerable<string> entityIds, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || _disposed) return Task.CompletedTask;
        foreach (var id in entityIds)
            _channel.Writer.TryWrite(new EnrichmentItem(id));
        return Task.CompletedTask;
    }

    /// <summary>
    /// How many items were dropped because the queue was full, and how many were abandoned unprocessed
    /// at shutdown. For tests and diagnostics.
    /// </summary>
    public (long Dropped, long AbandonedOnShutdown) Counters =>
        (Interlocked.Read(ref _dropped), Interlocked.Read(ref _abandonedOnShutdown));

    private void OnDropped(EnrichmentItem item)
    {
        var dropped = Interlocked.Increment(ref _dropped);

        // First one, then every hundredth: a full queue produces drops continuously, and logging each
        // would bury the signal in its own noise.
        if (dropped == 1 || dropped % 100 == 0)
        {
            _logger.LogWarning(
                "Enrichment queue full ({Capacity}); dropped {Dropped} item(s), most recently entity "
                + "{EntityId}. Those entities keep their un-enriched description. Raise "
                + "EnrichmentQueueOptions.MaxQueueCapacity or MaxConcurrency if this persists.",
                _options.MaxQueueCapacity, dropped, item.EntityId);
        }
    }

    private Task StartWorkersAsync(CancellationToken cancellationToken)
    {
        var workers = Enumerable
            .Range(0, _options.MaxConcurrency)
            .Select(_ => Task.Run(() => RunWorkerAsync(cancellationToken), cancellationToken));
        return Task.WhenAll(workers);
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _activeCount);
                try
                {
                    await ProcessItemAsync(item, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw; // shutdown — propagate to the outer catch and end the worker loop
                }
                catch (Exception ex)
                {
                    // A non-cancellation fault outside the per-provider try (e.g. a transient Neo4j error
                    // from GetByIdAsync/UpsertAsync, or Task.Delay) must NOT kill the worker — otherwise the
                    // only worker-level catch was for OCE, so the task faulted permanently and the entire
                    // queue silently stopped processing all future items. Log and continue with the next.
                    _logger.LogError(ex,
                        "Background enrichment failed for entity {EntityId}; skipping and continuing.",
                        item.EntityId);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeCount);
                }
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
    }

    private async Task ProcessItemAsync(EnrichmentItem item, CancellationToken cancellationToken)
    {
        var entity = await _entityRepository.GetByIdAsync(item.EntityId, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            _logger.LogWarning("Entity {EntityId} not found for background enrichment", item.EntityId);
            return;
        }

        var updated = entity;
        bool anySuccess = false;
        // Providers signal TRANSIENT failures (HTTP 429/5xx) by RETURNING a non-null Error/RateLimited
        // result, not by throwing — so success cannot be inferred from `result is not null`. Track whether
        // anything is worth retrying separately, otherwise a rate-limit/server-error is silently treated as
        // a successful enrichment and the entity is never retried.
        bool anyRetryable = false;

        foreach (var service in _enrichmentServices)
        {
            try
            {
                var result = await service.EnrichEntityAsync(entity.Name, entity.Type, cancellationToken).ConfigureAwait(false);
                var status = result?.Status;

                if (result is not null && status is null or EnrichmentStatus.Success)
                {
                    updated = updated with
                    {
                        Description = result.Summary ?? result.Description ?? updated.Description
                    };
                    anySuccess = true;
                    _logger.LogDebug("Enriched entity {EntityId} via {Provider}", entity.EntityId, result.Provider);
                }
                else if (result is null || status is EnrichmentStatus.Error or EnrichmentStatus.RateLimited)
                {
                    // Transient: worth a retry. (NotFound / Skipped are terminal — neither success nor retry.)
                    anyRetryable = true;
                    _logger.LogDebug(
                        "Enrichment provider {Provider} returned a transient {Status} for entity {EntityId}",
                        service.GetType().Name, status, entity.EntityId);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                anyRetryable = true;
                _logger.LogError(ex, "Enrichment provider {Provider} failed for entity {EntityId}",
                    service.GetType().Name, entity.EntityId);
            }
        }

        if (anySuccess)
        {
            await _entityRepository.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Nothing succeeded. Only retry on a TRANSIENT failure; if every provider returned a terminal
        // NotFound/Skipped (the entity is genuinely un-enrichable), do not loop — just stop.
        if (anyRetryable && item.RetryCount < _options.MaxRetries)
        {
            _logger.LogWarning(
                "All enrichment providers failed for entity {EntityId}; scheduling retry {Attempt}/{Max}",
                entity.EntityId, item.RetryCount + 1, _options.MaxRetries);

            await Task.Delay(_options.RetryDelay, cancellationToken).ConfigureAwait(false);
            _channel.Writer.TryWrite(item with { RetryCount = item.RetryCount + 1 });
        }
        else
        {
            _logger.LogWarning(
                "Enrichment dropped for entity {EntityId} after {TotalAttempts} attempt(s)",
                entity.EntityId, _options.MaxRetries + 1);
        }
    }

    /// <summary>
    /// Stops accepting work and reports anything still queued. Shared by both dispose paths.
    /// </summary>
    /// <returns>The number of items that will never be processed.</returns>
    /// <remarks>
    /// Read <b>before</b> cancelling, because after cancellation the workers stop draining and the count
    /// stops being meaningful. Zero is the normal case and says nothing; a non-zero count is the only
    /// evidence an operator gets that enrichment was thrown away at shutdown.
    /// </remarks>
    private long StopAcceptingAndCountAbandoned()
    {
        _channel.Writer.TryComplete();
        var abandoned = _channel.Reader.Count;
        if (abandoned > 0)
            Interlocked.Add(ref _abandonedOnShutdown, abandoned);
        _cts.Cancel();
        return abandoned;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Synchronous disposal cannot drain: blocking here would run the workers' async continuations on a
    /// thread that is waiting for them. So it reports what it is abandoning and returns. Hosts that
    /// care about the in-flight work should dispose asynchronously, which waits.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var abandoned = StopAcceptingAndCountAbandoned();
        if (abandoned > 0)
        {
            _logger.LogWarning(
                "Enrichment queue disposed synchronously with {Abandoned} item(s) still queued; they "
                + "will not be enriched. Dispose asynchronously to drain first.",
                abandoned);
        }

        // The CTS is deliberately NOT disposed here. The workers still hold its token, and disposing a
        // CancellationTokenSource while a consumer is registering a callback on that token throws
        // ObjectDisposedException inside the worker -- which faults _processingTask on a path where
        // nothing observes it. Cancellation has already been signalled, and that is what actually stops
        // them.
        //
        // Skipping Dispose costs nothing measurable here: the only unmanaged resource it releases is the
        // WaitHandle, which is allocated lazily and this class never asks for one (no Token.WaitHandle,
        // no linked source, no CancelAfter). Trading a real cross-thread race for an unallocated handle
        // is the right way round. DisposeAsync, which waits for the workers first, still disposes it.
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        var queuedAtShutdown = StopAcceptingAndCountAbandoned();

        try
        {
            await _processingTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: cancellation is how the workers are asked to stop.
        }
        catch (TimeoutException)
        {
            // NOT expected, and previously swallowed in silence. A worker that did not finish inside the
            // grace period is stuck in a provider call, and whatever it held is lost -- reporting that is
            // the difference between "enrichment is slow" and "enrichment is silently losing work".
            _logger.LogWarning(
                "Enrichment queue did not drain within 5s of shutdown; {Queued} item(s) were still "
                + "queued and at least one worker was still running. That work is abandoned.",
                queuedAtShutdown);
        }

        var (dropped, abandoned) = Counters;
        if (dropped > 0 || abandoned > 0)
        {
            _logger.LogInformation(
                "Enrichment queue lifetime: {Dropped} item(s) dropped while full, {Abandoned} abandoned "
                + "at shutdown.", dropped, abandoned);
        }

        // Safe here, unlike the synchronous path: the workers have either completed or timed out, so
        // nothing is registering new callbacks on this token.
        _cts.Dispose();
    }
}
