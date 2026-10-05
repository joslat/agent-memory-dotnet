using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace AgentMemory.RouterArena.Record;

/// <summary>
/// The same text, the same vector: a run that loads the same world for every turn (the storage jar) embeds its few hundred
/// memories once instead of once a turn. Only what is not cached is sent, in one batch; the model is deterministic, so the
/// vectors are the ones the model would have returned. Never disposes the generator it wraps.
/// </summary>
internal sealed class CachingEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> inner)
    : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly ConcurrentDictionary<string, Embedding<float>> _cache = new(StringComparer.Ordinal);

    public int Hits;
    public int Misses;

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var texts = values.ToList();
        var missing = texts.Where(t => !_cache.ContainsKey(t)).Distinct(StringComparer.Ordinal).ToList();
        Interlocked.Add(ref Hits, texts.Count - missing.Count);
        Interlocked.Add(ref Misses, missing.Count);
        if (missing.Count > 0)
        {
            var made = await inner.GenerateAsync(missing, options, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < missing.Count; i++)
                _cache[missing[i]] = made[i];
        }
        return new GeneratedEmbeddings<Embedding<float>>(texts.Select(t => _cache[t]));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);

    public void Dispose()
    {
    }
}
