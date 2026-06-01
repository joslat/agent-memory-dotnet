namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Centralizes embedding generation for the memory subsystem. The interface is intentionally
/// minimal — a single-text and a batch primitive. Domain-specific helpers
/// (<c>EmbedEntityAsync</c>, <c>EmbedFactAsync</c>, <c>EmbedQueryAsync</c>, …) are provided as
/// extension methods in <see cref="EmbeddingOrchestratorExtensions"/>.
/// </summary>
public interface IEmbeddingOrchestrator
{
    /// <summary>Generates an embedding vector for a single piece of text.</summary>
    /// <returns>The embedding vector, or an empty array when <paramref name="text"/> is blank or generation fails.</returns>
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);

    /// <summary>Generates embedding vectors for a batch of texts, preserving input order.</summary>
    /// <returns>
    /// One vector per input. Blank inputs yield an empty vector; on a generation failure every
    /// vector is returned empty so positional alignment with <paramref name="texts"/> is preserved.
    /// </returns>
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}
