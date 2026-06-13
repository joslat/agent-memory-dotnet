using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Read-side memory role: recalling assembled context for a query, including point-in-time recall.
/// Depend on this (rather than the full <see cref="IMemoryService"/>) when a component only reads memory.
/// </summary>
public interface IMemoryRecall
{
    /// <summary>
    /// Recalls memory context for a query.
    /// </summary>
    Task<RecallResult> RecallAsync(
        RecallRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Recalls memory context as it existed at a specific point in time (single-clock).
    /// Only entities, facts, and preferences that were created on or before <paramref name="asOf"/>
    /// and had not been invalidated by that time are included. Equivalent to the bitemporal overload
    /// with both clocks equal to <paramref name="asOf"/>.
    /// </summary>
    Task<RecallResult> RecallAsOfAsync(
        RecallRequest request,
        DateTimeOffset asOf,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Bitemporal point-in-time recall (D6) over two independent clocks. <paramref name="validAsOf"/>
    /// is the <b>valid-time</b> clock ("what was true in the world") and additionally bounds a fact's
    /// validity window; <paramref name="systemAsOf"/> is the <b>transaction-time</b> clock ("what the
    /// system had recorded / believed") and bounds every record's existence
    /// (<c>created_at</c>/<c>invalidated_at</c>). This lets you ask "what was true at <c>validAsOf</c>,
    /// as we knew it at <c>systemAsOf</c>" — e.g. reproduce a past decision, or audit a belief before a
    /// later correction. The single-clock <see cref="RecallAsOfAsync(RecallRequest,DateTimeOffset,CancellationToken)"/>
    /// delegates here with both clocks equal.
    /// </summary>
    Task<RecallResult> RecallAsOfAsync(
        RecallRequest request,
        DateTimeOffset validAsOf,
        DateTimeOffset systemAsOf,
        CancellationToken cancellationToken = default);
}
