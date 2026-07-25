using Microsoft.Extensions.AI;

namespace AgentMemory.Cli.Perf;

/// <summary>
/// A hashing bag-of-words embedder: deterministic across machines and processes, and — unlike a plain
/// hash of the whole string — <em>locality preserving</em>, so texts sharing words land near each other.
/// </summary>
/// <remarks>
/// <para>
/// Both properties are required, for different reasons.
/// </para>
/// <para>
/// <b>Deterministic</b> so retrieval results, and therefore any quality metric computed from them, are
/// stable across runs and machines. Note that <c>string.GetHashCode()</c> is <em>not</em> usable here:
/// .NET randomizes it per process, so a generator built on it produces different vectors on every run.
/// This uses FNV-1a, which is stable forever. (<c>StubEmbeddingGenerator</c> in Core has exactly that
/// per-process-randomness problem, which is one reason the harness does not reuse it.)
/// </para>
/// <para>
/// <b>Locality preserving</b> so a fixture can actually exercise the retrieval path. Vector recall
/// filters on <c>MinSimilarityScore</c> (0.7 by default); an embedder with no locality would put every
/// item at chance similarity to the query, every search would return nothing, and a scenario would
/// measure an empty recall while appearing to succeed.
/// </para>
/// <para>
/// Vectors are non-negative and L2-normalized, so cosine similarity lands in [0, 1].
/// </para>
/// </remarks>
public sealed class DeterministicEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly int _dimensions;

    public DeterministicEmbeddingGenerator(int dimensions) => _dimensions = dimensions;

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var value in values)
            result.Add(new Embedding<float>(Vector(value, _dimensions)));
        return Task.FromResult(result);
    }

    /// <summary>Builds the vector for <paramref name="text"/>. Public so fixtures can seed with it.</summary>
    public static float[] Vector(string text, int dimensions)
    {
        var vector = new float[dimensions];
        foreach (var token in Tokenize(text))
            vector[(int)(Fnv1a(token) % (uint)dimensions)] += 1f;

        var norm = 0d;
        for (var i = 0; i < dimensions; i++) norm += vector[i] * (double)vector[i];
        norm = Math.Sqrt(norm);

        // An empty or all-stopword text would divide by zero; fall back to a fixed unit vector so the
        // index never sees a zero vector (which it rejects) and behaviour stays deterministic.
        if (norm <= double.Epsilon)
        {
            vector[0] = 1f;
            return vector;
        }

        for (var i = 0; i < dimensions; i++) vector[i] = (float)(vector[i] / norm);
        return vector;
    }

    /// <summary>
    /// Words carrying no retrieval signal. Removed because this embedder weights every token equally,
    /// so without this a natural question ("Where did I stay in Lisbon, which hotel?") is mostly
    /// function words and the two words that matter get drowned out. A real embedder does not have that
    /// problem, so neither should the stand-in.
    /// </summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "but", "if", "of", "at", "by", "for", "with", "about", "into",
        "to", "from", "in", "on", "off", "over", "under", "is", "are", "was", "were", "be", "been",
        "being", "do", "does", "did", "doing", "have", "has", "had", "having", "i", "me", "my", "we",
        "our", "you", "your", "it", "its", "this", "that", "these", "those", "what", "which", "who",
        "whom", "when", "where", "why", "how", "any", "some", "no", "not", "so", "than", "then",
        "there", "here", "can", "could", "should", "would", "will", "shall", "may", "might", "must",
        "me", "am", "as", "s",
    };

    private static IEnumerable<string> Tokenize(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var isWord = i < text.Length && char.IsLetterOrDigit(text[i]);
            if (isWord && start < 0) start = i;
            else if (!isWord && start >= 0)
            {
                var token = Stem(text[start..i].ToLowerInvariant());
                start = -1;
                if (token.Length > 0 && !StopWords.Contains(token))
                    yield return token;
            }
        }
    }

    /// <summary>
    /// Conservative inflectional stemming: plural and verb-form endings only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this, "work" and "works" — or "peanut" and "peanuts" — are entirely different tokens and
    /// contribute nothing to similarity. That is not how any real embedding model behaves, and a
    /// stand-in that fails where the real thing succeeds makes the harness report retrieval failures
    /// that would never occur in production. Every conclusion drawn through it would inherit that.
    /// </para>
    /// <para>
    /// Deliberately limited to inflection (-s, -es, -ies, -ed, -ing). Derivational stemming
    /// ("allergy" → "allergic") needs a real algorithm, and guessing at one would trade a known small
    /// gap for an unpredictable one. Minimum stem length 4 keeps short words intact ("is", "was").
    /// </para>
    /// </remarks>
    private static string Stem(string token)
    {
        if (token.Length < 5) return token;

        if (token.EndsWith("ies", StringComparison.Ordinal) && token.Length > 5)
            return string.Concat(token.AsSpan(0, token.Length - 3), "y");
        if (token.EndsWith("ing", StringComparison.Ordinal) && token.Length > 6)
            return token[..^3];
        if (token.EndsWith("ed", StringComparison.Ordinal) && token.Length > 5)
            return token[..^2];
        if (token.EndsWith("es", StringComparison.Ordinal) && token.Length > 5)
            return token[..^2];
        if (token.EndsWith('s') && !token.EndsWith("ss", StringComparison.Ordinal))
            return token[..^1];

        return token;
    }

    /// <summary>FNV-1a: stable across processes, machines, and runtime versions.</summary>
    private static uint Fnv1a(string token)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;
        foreach (var c in token)
        {
            hash ^= c;
            hash *= prime;
        }
        return hash;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
