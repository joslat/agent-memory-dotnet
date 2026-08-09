namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// Represents a section of memory context with items of a specific type.
/// </summary>
/// <typeparam name="T">Type of items in this section.</typeparam>
public sealed record MemoryContextSection<T>
{
    /// <summary>
    /// Items in this section.
    /// </summary>
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();

    /// <summary>
    /// Ranked retrieval diagnostics for <see cref="Items"/>. Empty unless the recall explicitly requested
    /// diagnostics and the selected provider can return scores without issuing another retrieval query.
    /// </summary>
    public IReadOnlyList<MemoryContextRankedItem> RankedItems { get; init; } =
        Array.Empty<MemoryContextRankedItem>();

    /// <summary>
    /// Section-level metadata (e.g., retrieval method, scores).
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } =
        new Dictionary<string, object>();

    /// <summary>
    /// Empty section singleton.
    /// </summary>
    public static MemoryContextSection<T> Empty { get; } = new();
}

/// <summary>
/// Identifies an item in a ranked retrieval result without duplicating the item payload.
/// </summary>
/// <param name="ItemId">Stable identifier of the retrieved item.</param>
/// <param name="Score">Provider similarity score used for the retrieval ordering.</param>
/// <param name="RetrievalRank">One-based rank returned by the provider before context budgeting.</param>
/// <param name="ContextRank">One-based position among items that survived context budgeting.</param>
public sealed record MemoryContextRankedItem(
    string ItemId,
    double Score,
    int RetrievalRank,
    int ContextRank);
