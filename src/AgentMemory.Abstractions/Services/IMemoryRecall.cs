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
    /// Recalls memory context as it existed at a specific point in time.
    /// Only entities, facts, and preferences that were created on or before <paramref name="asOf"/>
    /// and had not been invalidated by that time are included.
    /// </summary>
    Task<RecallResult> RecallAsOfAsync(
        RecallRequest request,
        DateTimeOffset asOf,
        CancellationToken cancellationToken = default);
}
