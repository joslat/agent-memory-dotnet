using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Service for assembling memory context from multiple sources.
/// </summary>
public interface IMemoryContextAssembler
{
    /// <summary>
    /// Assembles memory context for a recall request.
    /// </summary>
    Task<MemoryContext> AssembleContextAsync(
        RecallRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Assembles memory context as it existed at a point in time over one or two clocks (D6).
    /// <paramref name="asOf"/> is the <b>valid-time</b> clock ("what was true in the world") and bounds a
    /// fact's validity window (<c>valid_from</c>/<c>valid_until</c>). The optional
    /// <paramref name="systemAsOf"/> is the <b>transaction-time</b> clock ("what the system had recorded")
    /// and bounds every node's existence window (<c>created_at</c>/<c>invalidated_at</c>); when omitted it
    /// defaults to <paramref name="asOf"/> (single-clock — both clocks equal). Messages, entities,
    /// preferences, and reasoning traces have no valid-time window, so they observe only the system clock.
    /// </summary>
    Task<MemoryContext> AssembleContextAsOfAsync(
        RecallRequest request,
        DateTimeOffset asOf,
        DateTimeOffset? systemAsOf = null,
        CancellationToken cancellationToken = default);
}
