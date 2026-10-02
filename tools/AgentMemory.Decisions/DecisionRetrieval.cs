using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace AgentMemory.Decisions;

internal interface IDecisionRetriever
{
    Task<IReadOnlyList<DecisionChunk>> RetrieveAsync(string question, int top, CancellationToken cancellationToken);
}

/// <summary>S0: keyword search (BM25) over the chunks; the grep baseline.</summary>
internal sealed partial class KeywordRetriever : IDecisionRetriever
{
    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "of", "to", "in", "on", "for", "and", "or", "is", "are", "was", "were", "be", "by", "with", "what",
        "which", "how", "why", "when", "does", "do", "did", "it", "its", "this", "that", "as", "at", "from", "now", "currently",
        "current", "decision", "decided", "cite", "source", "replaced", "replace", "use", "uses", "used",
    };

    private readonly IReadOnlyList<DecisionChunk> _chunks;
    private readonly List<Dictionary<string, int>> _terms;
    private readonly Dictionary<string, int> _documentFrequency = new(StringComparer.Ordinal);
    private readonly double _averageLength;

    internal KeywordRetriever(IReadOnlyList<DecisionChunk> chunks)
    {
        _chunks = chunks;
        _terms = [.. chunks.Select(chunk => Tokens(chunk.Text).GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal))];
        foreach (var term in _terms.SelectMany(terms => terms.Keys)) _documentFrequency[term] = _documentFrequency.GetValueOrDefault(term) + 1;
        _averageLength = _terms.Count == 0 ? 1 : _terms.Average(terms => terms.Values.Sum());
    }

    public Task<IReadOnlyList<DecisionChunk>> RetrieveAsync(string question, int top, CancellationToken cancellationToken)
    {
        const double k1 = 1.2, b = 0.75;
        var query = Tokens(question).Distinct().ToList();
        var n = _chunks.Count;
        IReadOnlyList<DecisionChunk> ranked = [.. _terms
            .Select((terms, i) =>
            {
                var length = terms.Values.Sum();
                var score = query.Sum(term =>
                {
                    if (!terms.TryGetValue(term, out var f)) return 0;
                    var df = _documentFrequency[term];
                    var idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
                    return idf * f * (k1 + 1) / (f + k1 * (1 - b + b * length / _averageLength));
                });
                return (Chunk: _chunks[i], Score: score);
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(top)
            .Select(x => x.Chunk)];
        return Task.FromResult(ranked);
    }

    internal static IEnumerable<string> Tokens(string text) =>
        Word().Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(t => t.Length > 1 && !Stop.Contains(t));

    [GeneratedRegex(@"[a-z0-9]+")]
    private static partial Regex Word();
}

/// <summary>S1: cosine over embeddings of the chunks; the vector baseline. Embeddings are cached by text hash.</summary>
internal sealed class VectorRetriever : IDecisionRetriever
{
    private readonly IReadOnlyList<DecisionChunk> _chunks;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly float[][] _vectors;

    private VectorRetriever(IReadOnlyList<DecisionChunk> chunks, IEmbeddingGenerator<string, Embedding<float>> embedder, float[][] vectors)
    {
        _chunks = chunks;
        _embedder = embedder;
        _vectors = vectors;
    }

    internal static async Task<VectorRetriever> CreateAsync(
        IReadOnlyList<DecisionChunk> chunks, IEmbeddingGenerator<string, Embedding<float>> embedder, string cachePath,
        CancellationToken cancellationToken)
    {
        var cache = File.Exists(cachePath)
            ? JsonSerializer.Deserialize<Dictionary<string, float[]>>(await File.ReadAllTextAsync(cachePath, cancellationToken).ConfigureAwait(false)) ?? []
            : new Dictionary<string, float[]>();
        var missing = chunks.Where(chunk => !cache.ContainsKey(Hash(chunk.Text))).ToList();
        foreach (var batch in missing.Chunk(16))
        {
            var embedded = await embedder.GenerateAsync(batch.Select(chunk => chunk.Text), cancellationToken: cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < batch.Length; i++) cache[Hash(batch[i].Text)] = embedded[i].Vector.ToArray();
        }
        if (missing.Count > 0)
            await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(cache), cancellationToken).ConfigureAwait(false);
        return new VectorRetriever(chunks, embedder, [.. chunks.Select(chunk => cache[Hash(chunk.Text)])]);
    }

    public async Task<IReadOnlyList<DecisionChunk>> RetrieveAsync(string question, int top, CancellationToken cancellationToken)
    {
        var query = (await _embedder.GenerateAsync([question], cancellationToken: cancellationToken).ConfigureAwait(false))[0].Vector.ToArray();
        return [.. _vectors
            .Select((vector, i) => (Chunk: _chunks[i], Score: Cosine(query, vector)))
            .OrderByDescending(x => x.Score)
            .Take(top)
            .Select(x => x.Chunk)];
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32].ToLowerInvariant();
}
