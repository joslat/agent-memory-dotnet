namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Provider for generating text embeddings.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>
    /// Generates an embedding for a single text.
    /// </summary>
    Task<float[]> GenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates embeddings for multiple texts in batch.
    /// </summary>
    Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(
        IEnumerable<string> texts,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the dimensionality of embeddings produced by this provider.
    /// </summary>
    int EmbeddingDimensions { get; }
}
