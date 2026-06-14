namespace AgentMemory.Benchmarks.Infrastructure;

/// <summary>
/// Produces deterministic, unit-length embedding vectors from an integer seed. Same seed ⇒ same vector,
/// so a benchmark run is reproducible. Content is meaningless — only the shape/magnitude matters for
/// exercising the vector index, not semantic relevance.
/// </summary>
internal static class DeterministicEmbedding
{
    /// <summary>Returns a normalized <c>float[dimensions]</c> derived deterministically from <paramref name="seed"/>.</summary>
    public static float[] Create(int seed, int dimensions)
    {
        // A small LCG seeded by `seed` — avoids Random's nondeterminism across runtimes and needs no shared state.
        ulong state = unchecked((ulong)seed * 2862933555777941757UL + 3037000493UL);
        var vector = new float[dimensions];
        double sumSq = 0;
        for (var i = 0; i < dimensions; i++)
        {
            state = unchecked(state * 6364136223846793005UL + 1442695040888963407UL);
            // Map the high bits to [-1, 1).
            var v = (double)(state >> 11) / (1UL << 53) * 2.0 - 1.0;
            vector[i] = (float)v;
            sumSq += v * v;
        }

        var norm = Math.Sqrt(sumSq);
        if (norm > 0)
        {
            var inv = (float)(1.0 / norm);
            for (var i = 0; i < dimensions; i++) vector[i] *= inv;
        }
        return vector;
    }
}
