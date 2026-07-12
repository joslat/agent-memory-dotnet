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
    /// Point-in-time recall over one or two clocks. <paramref name="asOf"/> is the <b>valid-time</b> clock
    /// ("what was true in the world") and additionally bounds a fact's validity window. The optional
    /// <paramref name="systemAsOf"/> is the <b>transaction-time</b> clock ("what the system had recorded /
    /// believed") and bounds every record's existence (<c>created_at</c>/<c>invalidated_at</c>); when
    /// omitted it defaults to <paramref name="asOf"/> (single-clock recall — both clocks equal). Passing
    /// both lets you ask "what was true at <c>asOf</c>, as we knew it at <c>systemAsOf</c>" — e.g. reproduce
    /// a past decision, or audit a belief before a later correction. Returns the recent messages, entities,
    /// facts, preferences, and similar reasoning traces that existed at the requested time(s).
    /// </summary>
    Task<RecallResult> RecallAsOfAsync(
        RecallRequest request,
        DateTimeOffset asOf,
        DateTimeOffset? systemAsOf = null,
        CancellationToken cancellationToken = default);
}
