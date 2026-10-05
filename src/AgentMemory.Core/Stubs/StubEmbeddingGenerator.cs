using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Core.Stubs;

/// <summary>
/// Stub embedding generator: returns deterministic random embeddings.
/// Replace with a real <see cref="IEmbeddingGenerator{String, Embedding}"/> implementation in production.
/// </summary>
public sealed class StubEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly ILogger<StubEmbeddingGenerator> _logger;
    private readonly int _dimensions;

    /// <summary>
    /// Initializes a new instance of the <see cref="StubEmbeddingGenerator"/> class.
    /// </summary>
    /// <param name="logger">The logger used to warn when the stub generator is invoked.</param>
    /// <param name="dimensions">The dimensionality of the generated embedding vectors.</param>
    public StubEmbeddingGenerator(ILogger<StubEmbeddingGenerator> logger, int dimensions = 1536)
    {
        _logger = logger;
        _dimensions = dimensions;
    }

    /// <inheritdoc/>
    public EmbeddingGeneratorMetadata Metadata =>
        new("stub");

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this) ? this : null;

    /// <inheritdoc/>
    public void Dispose() { }

    private float[] GenerateVector(string text)
    {
        // Same text → same vector, in every process. string.GetHashCode() is randomised per process, so the same
        // text got a different vector in each test run, and on small test dimensions two different names were close
        // enough to merge in one run and not in the next (PLAN 40.40). A seed from the text's SHA-256 is stable.
        var seed = BitConverter.ToInt32(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)), 0);
        var rng = new Random(seed);
        var vector = new float[_dimensions];
        for (var i = 0; i < _dimensions; i++)
            vector[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
        return vector;
    }
}
