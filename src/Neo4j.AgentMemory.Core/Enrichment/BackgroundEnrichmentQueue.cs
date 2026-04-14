using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Repositories;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Core.Enrichment;

/// <summary>
/// Represents a single queued enrichment work item.
/// </summary>
internal record EnrichmentItem(string EntityId, int RetryCount = 0);

/// <summary>
/// Non-blocking background queue that runs enrichment providers asynchronously.
/// Uses <see cref="System.Threading.Channels.Channel{T}"/> with DropOldest overflow,
/// a fixed pool of worker tasks (no IHostedService), and configurable retry logic.
/// </summary>
public sealed class BackgroundEnrichmentQueue : IBackgroundEnrichmentQueue, IDisposable, IAsyncDisposable
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

    public int QueueDepth => _options.Enabled ? _channel.Reader.Count : 0;
    public bool IsProcessing => _activeCount > 0;

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
        _channel = Channel.CreateBounded<EnrichmentItem>(channelOptions);

        _processingTask = _options.Enabled
            ? StartWorkersAsync(_cts.Token)
            : Task.CompletedTask;
    }

    public Task EnqueueAsync(string entityId, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || _disposed) return Task.CompletedTask;
        _channel.Writer.TryWrite(new EnrichmentItem(entityId));
        return Task.CompletedTask;
    }

    public Task EnqueueBatchAsync(IEnumerable<string> entityIds, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || _disposed) return Task.CompletedTask;
        foreach (var id in entityIds)
            _channel.Writer.TryWrite(new EnrichmentItem(id));
        return Task.CompletedTask;
    }

    private Task StartWorkersAsync(CancellationToken ct)
    {
        var workers = Enumerable
            .Range(0, _options.MaxConcurrency)
            .Select(_ => Task.Run(() => RunWorkerAsync(ct), ct));
        return Task.WhenAll(workers);
    }

    private async Task RunWorkerAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _activeCount);
                try
                {
                    await ProcessItemAsync(item, ct).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeCount);
                }
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
    }

    private async Task ProcessItemAsync(EnrichmentItem item, CancellationToken ct)
    {
        var entity = await _entityRepository.GetByIdAsync(item.EntityId, ct).ConfigureAwait(false);
        if (entity is null)
        {
            _logger.LogWarning("Entity {EntityId} not found for background enrichment", item.EntityId);
            return;
        }

        var updated = entity;
        bool anySuccess = false;

        foreach (var service in _enrichmentServices)
        {
            try
            {
                var result = await service.EnrichEntityAsync(entity.Name, entity.Type, ct).ConfigureAwait(false);
                if (result is not null)
                {
                    updated = updated with
                    {
                        Description = result.Summary ?? result.Description ?? updated.Description
                    };
                    anySuccess = true;
                    _logger.LogDebug("Enriched entity {EntityId} via {Provider}", entity.EntityId, result.Provider);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Enrichment provider {Provider} failed for entity {EntityId}",
                    service.GetType().Name, entity.EntityId);
            }
        }

        if (anySuccess)
        {
            await _entityRepository.UpsertAsync(updated, ct).ConfigureAwait(false);
            return;
        }

        if (item.RetryCount < _options.MaxRetries)
        {
            _logger.LogWarning(
                "All enrichment providers failed for entity {EntityId}; scheduling retry {Attempt}/{Max}",
                entity.EntityId, item.RetryCount + 1, _options.MaxRetries);

            await Task.Delay(_options.RetryDelay, ct).ConfigureAwait(false);
            _channel.Writer.TryWrite(item with { RetryCount = item.RetryCount + 1 });
        }
        else
        {
            _logger.LogWarning(
                "Enrichment dropped for entity {EntityId} after {TotalAttempts} attempt(s)",
                entity.EntityId, _options.MaxRetries + 1);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _channel.Writer.TryComplete();
        _cts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _channel.Writer.TryComplete();
        try
        {
            await _processingTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }
        _cts.Dispose();
    }
}
