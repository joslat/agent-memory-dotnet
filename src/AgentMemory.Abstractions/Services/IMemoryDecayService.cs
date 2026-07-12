using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Service responsible for memory decay scoring and pruning of stale memories.
/// </summary>
public interface IMemoryDecayService
{
    /// <summary>
    /// Removes memory nodes (entities, facts, preferences) whose computed retention score falls below
    /// the configured minimum threshold. When <paramref name="scope"/> is supplied (R1) the prune only
    /// removes the owner's <b>own</b> nodes — never another owner's, and never shared/global ones; null
    /// means an unscoped (admin/global) prune across all owners.
    /// </summary>
    /// <returns>Total number of nodes pruned.</returns>
    Task<int> PruneExpiredMemoriesAsync(MemoryScope? scope = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Calculates the retention score for a single memory node.
    /// </summary>
    /// <param name="nodeId">The id property of the node.</param>
    /// <param name="nodeKind">The kind of memory node (Entity, Fact, or Preference).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Computed retention score in [0, ∞).</returns>
    Task<double> CalculateRetentionScoreAsync(string nodeId, MemoryNodeKind nodeKind, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bumps <c>last_accessed_at</c> and increments <c>access_count</c> on a memory node.
    /// </summary>
    Task UpdateAccessTimestampAsync(string nodeId, MemoryNodeKind nodeKind, CancellationToken cancellationToken = default);
}
