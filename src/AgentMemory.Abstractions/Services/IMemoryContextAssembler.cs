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
    /// Assembles memory context as it existed at a specific point in time (single-clock).
    /// Equivalent to the bitemporal overload with both clocks equal to <paramref name="asOf"/>.
    /// </summary>
    Task<MemoryContext> AssembleContextAsOfAsync(
        RecallRequest request,
        DateTimeOffset asOf,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Bitemporal context assembly (D6): assembles memory as it existed on two independent clocks.
    /// <paramref name="validAsOf"/> is the <b>valid-time</b> clock ("what was true in the world") and
    /// additionally bounds a fact's validity window (<c>valid_from</c>/<c>valid_until</c>);
    /// <paramref name="systemAsOf"/> is the <b>transaction-time</b> clock ("what the system had recorded")
    /// and bounds every node's existence window (<c>created_at</c>/<c>invalidated_at</c>) — messages,
    /// entities, preferences, and reasoning traces have no valid-time window, so they observe only
    /// <paramref name="systemAsOf"/>. The single-clock overload delegates here with both clocks equal.
    /// </summary>
    Task<MemoryContext> AssembleContextAsOfAsync(
        RecallRequest request,
        DateTimeOffset validAsOf,
        DateTimeOffset systemAsOf,
        CancellationToken cancellationToken = default);
}
