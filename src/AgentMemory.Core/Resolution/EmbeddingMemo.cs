using AgentMemory.Abstractions.Services;

namespace AgentMemory.Core.Resolution;

/// <summary>
/// An <see cref="IEmbeddingOrchestrator"/> for one resolution that remembers what it embedded, so the
/// vector the semantic matcher computed for a mention can be given to the entity created for it
/// instead of being requested again by persistence.
/// </summary>
/// <remarks>
/// Scoped to a single <c>ResolveEntityCoreAsync</c> call, so it is never shared across threads and
/// never outlives the texts it holds. Failed embeddings (empty vectors) are passed through but not
/// remembered: persistence then retries them exactly as before.
/// </remarks>
internal sealed class EmbeddingMemo(IEmbeddingOrchestrator inner, Func<string, float[]?>? prepared = null)
    : IEmbeddingOrchestrator
{
    private readonly Dictionary<string, float[]> _vectors = new(StringComparer.Ordinal);

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_vectors.TryGetValue(text, out var known))
            return known;
        // Vectors the batch embedded up front, in one request, for exactly the names that would get here.
        if (prepared?.Invoke(text) is { Length: > 0 } ready)
            return _vectors[text] = ready;
        var vector = await inner.EmbedAsync(text, cancellationToken).ConfigureAwait(false);
        if (vector is { Length: > 0 })
            _vectors[text] = vector;
        return vector;
    }

    public Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default) =>
        inner.EmbedBatchAsync(texts, cancellationToken);

    /// <summary>The vector already computed for exactly this text, or null.</summary>
    public float[]? Computed(string text) => _vectors.GetValueOrDefault(text);
}
