using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Compiles and serves the per-owner working-memory block.
/// </summary>
/// <remarks>
/// A new interface rather than a default interface method on an existing one: nothing existing is
/// touched, so this is additive and SemVer-safe. Implementations own their own <c>Enabled</c> gate so
/// the service can be registered unconditionally and still honour <c>IOptions</c> reconfiguration —
/// the reranker pattern.
/// </remarks>
public interface IWorkingMemoryService
{
    /// <summary>
    /// Recompiles and stores the owner's block. Full rebuild; there is no partial invalidation.
    /// </summary>
    /// <remarks>
    /// Awaited inline by its callers rather than fire-and-forget, because the contract the staleness
    /// canary tests is "after the write call returns, the block is current". A few milliseconds of
    /// write latency is the price of that contract, on a write that already took about a second of
    /// extraction.
    /// </remarks>
    Task RebuildAsync(string ownerId, CancellationToken cancellationToken = default);

    /// <summary>The stored block, or null when none exists or the tier is disabled.</summary>
    Task<WorkingMemoryBlock?> GetAsync(string ownerId, CancellationToken cancellationToken = default);

    /// <summary>Removes the stored block. Used when a rebuild fails and staleness must not persist.</summary>
    Task ClearAsync(string ownerId, CancellationToken cancellationToken = default);
}
