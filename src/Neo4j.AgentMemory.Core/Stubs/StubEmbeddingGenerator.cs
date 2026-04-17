using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Neo4j.AgentMemory.Core.Stubs;

/// <summary>
/// Stub embedding generator: returns deterministic random embeddings.
/// Replace with a real <see cref="IEmbeddingGenerator{String, Embedding}"/> implementation in production.
/// </summary>
public sealed class StubEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly ILogger<StubEmbeddingGenerator> _logger;
    private readonly int _dimensions;

    public StubEmbeddingGenerator(ILogger<StubEmbeddingGenerator> logger, int dimensions = 1536)
    {
        _logger = logger;
        _dimensions = dimensions;
    }

    public EmbeddingGeneratorMetadata Metadata =>
        new("stub");

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("StubEmbeddingGenerator is in use — returning deterministic random vectors. Replace with a real provider.");
        var embeddings = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var text in values)
        {
            embeddings.Add(new Embedding<float>(GenerateVector(text)));
        }
        return Task.FromResult(embeddings);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    private float[] GenerateVector(string text)
    {
        // Deterministic seed from text hash: same input → same vector.
        var seed = text.GetHashCode();
        var rng = new Random(seed);
        var vector = new float[_dimensions];
        for (var i = 0; i < _dimensions; i++)
            vector[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
        return vector;
    }
}
