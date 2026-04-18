namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Service responsible for memory decay scoring and pruning of stale memories.
/// </summary>
public interface IMemoryDecayService
{
    /// <summary>
    /// Removes all memory nodes (entities, facts, preferences) for the given session
    /// whose computed retention score falls below the configured minimum threshold.
    /// </summary>
    /// <returns>Total number of nodes pruned.</returns>
    Task<int> PruneExpiredMemoriesAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Calculates the retention score for a single memory node.
    /// </summary>
    /// <param name="nodeId">The id property of the node.</param>
    /// <param name="nodeLabel">The Neo4j label (Entity, Fact, or Preference).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Computed retention score in [0, ∞).</returns>
    Task<double> CalculateRetentionScoreAsync(string nodeId, string nodeLabel, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bumps <c>last_accessed_at</c> and increments <c>access_count</c> on a memory node.
    /// </summary>
    Task UpdateAccessTimestampAsync(string nodeId, string nodeLabel, CancellationToken cancellationToken = default);
}
