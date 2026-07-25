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
    /// <param name="nodeId">The id property of the node.</param>
    /// <param name="nodeKind">The kind of memory node (Entity, Fact, or Preference).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpdateAccessTimestampAsync(string nodeId, MemoryNodeKind nodeKind, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bumps <c>last_accessed_at</c> and increments <c>access_count</c> on many memory nodes at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exists because recall calls this once per recalled item on the pre-model critical path. Measured
    /// at shipped defaults, the per-item form issued <b>25 separate write transactions</b> — 81% of a
    /// default recall's database round trips — before the model was invoked. Writes are leader-bound in
    /// a cluster, so that is load on the least scalable node in the topology, paid before the first
    /// token, and it grows one-for-one with recall limits.
    /// </para>
    /// <para>
    /// The default implementation loops <see cref="UpdateAccessTimestampAsync"/>, preserving the exact
    /// behaviour of any existing implementation that does not override it — which is why this was added
    /// as a default interface method rather than a plain interface member: adding a required member to a
    /// public interface would break every third-party implementer under SemVer.
    /// </para>
    /// <para>
    /// Implementations that can batch <b>should</b> override this. Semantics must be identical to
    /// calling the per-item method once for each entry: every node's timestamp updated, access count
    /// incremented, and any audit record written. Duplicate entries should be applied once.
    /// </para>
    /// </remarks>
    /// <param name="nodes">The nodes to touch. An empty collection is a no-op.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    async Task UpdateAccessTimestampsAsync(
        IReadOnlyCollection<(string NodeId, MemoryNodeKind NodeKind)> nodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        foreach (var (nodeId, nodeKind) in nodes)
            await UpdateAccessTimestampAsync(nodeId, nodeKind, cancellationToken).ConfigureAwait(false);
    }
}
