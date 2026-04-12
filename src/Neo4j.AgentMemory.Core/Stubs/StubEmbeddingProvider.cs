using Microsoft.Extensions.Logging;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Core.Stubs;

/// <summary>
/// Phase 1 stub: returns deterministic random embeddings. Replace in Phase 2 with a real provider.
/// </summary>
public sealed class StubEmbeddingProvider : IEmbeddingProvider
{
    private readonly ILogger<StubEmbeddingProvider> _logger;

    public int EmbeddingDimensions { get; }

    public StubEmbeddingProvider(ILogger<StubEmbeddingProvider> logger, int dimensions = 1536)
    {
        _logger = logger;
        EmbeddingDimensions = dimensions;
    }

    public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("StubEmbeddingProvider is in use — returning deterministic random vector. Replace with a real provider in Phase 2.");
        return Task.FromResult(GenerateVector(text));
    }

    public Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(
        IEnumerable<string> texts,
        CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("StubEmbeddingProvider is in use — returning deterministic random vectors. Replace with a real provider in Phase 2.");
        IReadOnlyList<float[]> result = texts.Select(GenerateVector).ToList();
        return Task.FromResult(result);
    }

    private float[] GenerateVector(string text)
    {
        // Deterministic seed from text hash: same input → same vector.
        var seed = text.GetHashCode();
        var rng = new Random(seed);
        var vector = new float[EmbeddingDimensions];
        for (var i = 0; i < EmbeddingDimensions; i++)
            vector[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
        return vector;
    }
}
