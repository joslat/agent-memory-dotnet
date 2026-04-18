namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Wraps a page of results with a flag indicating whether more data exists.
/// Enables the N+1 pagination pattern: repositories fetch one extra item to detect
/// a next page without a separate COUNT(*) round-trip.
/// </summary>
/// <typeparam name="T">The item type.</typeparam>
public sealed record PagedResult<T>
{
    /// <summary>The items on this page (at most the requested page size).</summary>
    public IReadOnlyList<T> Items { get; init; }

    /// <summary>True when at least one more item exists beyond this page.</summary>
    public bool HasNextPage { get; init; }

    /// <summary>Initializes a new <see cref="PagedResult{T}"/> with the given items and next-page flag.</summary>
    /// <param name="items">The items on this page.</param>
    /// <param name="hasNextPage">True when at least one more item exists beyond this page.</param>
    public PagedResult(IReadOnlyList<T> items, bool hasNextPage)
    {
        Items = items;
        HasNextPage = hasNextPage;
    }
}
