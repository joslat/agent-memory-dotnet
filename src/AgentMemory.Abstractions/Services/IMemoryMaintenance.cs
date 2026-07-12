using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Maintenance memory role: lifecycle and bulk operations such as clearing a session and
/// backfilling embeddings. Depend on this when a component only performs upkeep.
/// </summary>
public interface IMemoryMaintenance
{
    /// <summary>
    /// Clears short-term memory for a session (messages + conversations) and the session's reasoning
    /// traces. The trace delete is confined to one R1 owner bucket: null <paramref name="ownerId"/> ⇒
    /// shared/global traces only, otherwise that owner's traces — so owner A's clear never deletes owner B's
    /// traces. Pass the calling owner in multi-tenant hosts to isolate the clear.
    /// </summary>
    Task ClearSessionAsync(
        string sessionId,
        string? ownerId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates and persists embeddings for all nodes of the given <paramref name="nodeKind"/> that
    /// currently have a null embedding. Processes in batches of <paramref name="batchSize"/>.
    /// </summary>
    /// <param name="nodeKind">The kind of memory node to backfill (Entity, Fact, or Preference).</param>
    /// <param name="batchSize">Number of nodes to process per batch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The number of nodes actually updated — i.e. for which a non-empty embedding was generated and
    /// persisted. Nodes whose embedding generation degraded to an empty vector (and were therefore skipped)
    /// are not counted.
    /// </returns>
    Task<int> GenerateEmbeddingsBatchAsync(
        MemoryNodeKind nodeKind,
        int batchSize = 100,
        CancellationToken cancellationToken = default);
}
