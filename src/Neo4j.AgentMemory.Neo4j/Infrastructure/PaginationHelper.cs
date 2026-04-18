using Neo4j.AgentMemory.Abstractions.Domain;

namespace Neo4j.AgentMemory.Neo4j.Infrastructure;

/// <summary>
/// Applies the N+1 pagination pattern: the caller requests one extra item to determine
/// whether a next page exists, avoiding a separate COUNT(*) round-trip.
/// </summary>
internal static class PaginationHelper
{
    /// <summary>
    /// Inspects a raw result list that was fetched with <c>requestedPageSize + 1</c> as the
    /// database limit.  If the list contains more than <paramref name="requestedPageSize"/> items
    /// the extra item is removed and <see cref="PagedResult{T}.HasNextPage"/> is set to
    /// <c>true</c>; otherwise <c>HasNextPage</c> is <c>false</c>.
    /// </summary>
    public static PagedResult<T> ApplyPagination<T>(List<T> items, int requestedPageSize)
    {
        var hasNextPage = items.Count > requestedPageSize;
        if (hasNextPage)
            items.RemoveAt(items.Count - 1);
        return new PagedResult<T>(items, hasNextPage);
    }
}
