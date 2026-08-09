namespace AgentMemory.Core.Extraction;

/// <summary>
/// Internal opt-in capability for repositories that can atomically upsert one memory kind as a batch.
/// The public repository contracts remain unchanged, so third-party implementations keep their current
/// item-at-a-time behavior unless the built-in pipeline can prove batch semantics are available.
/// </summary>
internal interface IBatchMemoryRepository<T>
{
    Task<IReadOnlyList<T>> UpsertBatchAsync(
        IReadOnlyList<T> items,
        CancellationToken cancellationToken = default);
}
