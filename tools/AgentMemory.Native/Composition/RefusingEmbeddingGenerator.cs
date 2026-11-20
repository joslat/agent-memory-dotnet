using Microsoft.Extensions.AI;

namespace AgentMemory.Native.Composition;

/// <summary>
/// The embedding generator of the read-only native profile: every recall supplies its query vector, so a call that
/// reaches this generator means a path asked for an embedding the profile does not provide. It fails loudly rather
/// than calling a provider nobody configured.
/// </summary>
internal sealed class RefusingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    /// <summary>Raised when any code path asks this profile to produce an embedding.</summary>
    internal const string Message =
        "The read-only native profile does not generate embeddings: every recall must supply its query vector.";

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(Message);

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
