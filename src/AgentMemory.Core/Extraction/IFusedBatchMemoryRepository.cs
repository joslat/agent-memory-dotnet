namespace AgentMemory.Core.Extraction;

/// <summary>
/// Internal opt-in capability for repositories that can fold an item's node mutation, embedding,
/// provider-specific labels/location, and provenance edges into one bounded batch query. The public
/// repository contracts and the legacy <see cref="IBatchMemoryRepository{T}"/> path remain unchanged.
/// </summary>
internal interface IFusedBatchMemoryRepository<T>
{
    Task<IReadOnlyList<T>> UpsertFusedBatchAsync(
        IReadOnlyList<T> items,
        CancellationToken cancellationToken = default);
}
