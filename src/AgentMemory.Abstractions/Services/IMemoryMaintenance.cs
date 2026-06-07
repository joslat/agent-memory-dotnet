namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Maintenance memory role: lifecycle and bulk operations such as clearing a session and
/// backfilling embeddings. Depend on this when a component only performs upkeep.
/// </summary>
public interface IMemoryMaintenance
{
    /// <summary>
    /// Clears all memory for a session.
    /// </summary>
    Task ClearSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates and persists embeddings for all nodes of the given label that
    /// currently have a null embedding. Processes in batches of <paramref name="batchSize"/>.
    /// Supported labels: <c>Entity</c>, <c>Fact</c>, <c>Preference</c>.
    /// </summary>
    /// <returns>Total number of nodes updated.</returns>
    Task<int> GenerateEmbeddingsBatchAsync(
        string nodeLabel,
        int batchSize = 100,
        CancellationToken cancellationToken = default);
}
