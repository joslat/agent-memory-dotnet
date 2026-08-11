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
    /// Why this section holds what it holds. <see langword="null"/> unless the recall requested
    /// diagnostics.
    /// </summary>
    /// <remarks>
    /// <see cref="Items"/> being empty has at least three causes that need opposite responses, and
    /// nothing recorded which one applied: the section was never searched, the owner genuinely holds
    /// nothing, or candidates existed and every one fell below <c>MinSimilarityScore</c> — or, on an
    /// owner-scoped vector search, survived the index's global top-K only to be filtered away. A
    /// measured example: one benchmark question retrieved <b>2 facts from a 710-fact graph</b> with the
    /// answer demonstrably present, and no artifact could say which of those it was.
    /// </remarks>
    public MemoryContextSectionDiagnostics? Diagnostics { get; init; }

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

/// <summary>
/// Why a recall section holds what it holds.
/// </summary>
/// <param name="Searched">
/// Whether a retrieval actually ran for this section. <see langword="false"/> means the section was
/// excluded — by a task-aware recall policy, a zero limit, or a missing query embedding — and its
/// emptiness says nothing about the store.
/// </param>
/// <param name="RequestedLimit">The limit the retrieval asked for, so a short result is visible as short.</param>
/// <param name="Returned">How many items the retrieval returned, before context budgeting.</param>
/// <param name="TopScore">
/// Highest provider score among the returned items, or <see langword="null"/> when nothing was
/// returned or the provider cannot supply scores. Never a placeholder: a section that cannot be
/// scored reports null rather than zero, because zero is a real score.
/// </param>
/// <param name="LowestScore">Lowest returned score, or <see langword="null"/> under the same rules.</param>
/// <param name="MinimumScore">The similarity floor in force, so "all below threshold" is checkable.</param>
public sealed record MemoryContextSectionDiagnostics(
    bool Searched,
    int RequestedLimit,
    int Returned,
    double? TopScore,
    double? LowestScore,
    double MinimumScore)
{
    /// <summary>
    /// The retrieval ran and came back with nothing — as opposed to never having run.
    /// </summary>
    public bool SearchedAndEmpty => Searched && Returned == 0;

    /// <summary>
    /// The retrieval ran and returned fewer items than it asked for.
    /// </summary>
    /// <remarks>
    /// On an owner-scoped vector search this is the shape that matters most: the index picks a global
    /// top-K and the owner filter is applied afterwards, so a short result can mean an owner was
    /// crowded out by other tenants rather than that it holds little. The escalation path fires only
    /// on a <i>total</i> zero, so this case is otherwise invisible.
    /// </remarks>
    public bool SearchedAndShort => Searched && Returned < RequestedLimit;
}
