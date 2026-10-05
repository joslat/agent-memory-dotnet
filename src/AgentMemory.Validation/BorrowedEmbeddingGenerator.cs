using Microsoft.Extensions.AI;

namespace AgentMemory.Validation;

/// <summary>
/// A caller's embedding generator lent to one pack's container: every call goes to it, and disposing the pack leaves it
/// alone, so the same generator serves every pack a runner loads.
/// </summary>
internal sealed class BorrowedEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> inner)
    : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
        inner.GenerateAsync(values, options, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);

    /// <summary>Nothing: the generator belongs to whoever lent it.</summary>
    public void Dispose()
    {
    }
}
