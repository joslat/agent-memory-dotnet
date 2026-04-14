namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Async, non-blocking queue that runs entity enrichment in the background.
/// </summary>
public interface IBackgroundEnrichmentQueue
{
    /// <summary>
    /// Enqueues an entity for background enrichment.
    /// Returns immediately — enrichment runs asynchronously.
    /// </summary>
    Task EnqueueAsync(string entityId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enqueues multiple entities for background enrichment.
    /// </summary>
    Task EnqueueBatchAsync(IEnumerable<string> entityIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current queue depth (number of pending items waiting for a worker).
    /// </summary>
    int QueueDepth { get; }

    /// <summary>
    /// Gets whether the queue is currently processing items.
    /// </summary>
    bool IsProcessing { get; }
}
