namespace AgentMemory.Abstractions.Exceptions;

/// <summary>
/// One Neo4j vector index whose configured dimensionality differs from the embedder's.
/// </summary>
/// <param name="IndexName">The vector index name (e.g. <c>fact_embedding_idx</c>).</param>
/// <param name="ActualDimensions">The <c>vector.dimensions</c> the index was created with.</param>
public sealed record VectorIndexDimension(string IndexName, int ActualDimensions);

/// <summary>
/// Thrown at schema bootstrap when one or more <b>existing</b> Neo4j vector indexes were created with a
/// different dimensionality than the currently-configured embedding model
/// (<see cref="MemoryErrorCodes.EmbeddingDimensionMismatch"/>).
/// </summary>
/// <remarks>
/// <para>
/// <c>CREATE VECTOR INDEX ... IF NOT EXISTS</c> is a no-op when the index already exists, so switching
/// embedding models (e.g. 1536 → 3072 dimensions) silently leaves the old index in place. Without this
/// guard the mismatch would surface much later as an opaque query-time failure on the first vector
/// search. Validating at startup turns that into an actionable fail-fast: the message lists every
/// offending index and the expected dimensionality so the operator can drop/recreate it (or re-point
/// the embedder).
/// </para>
/// </remarks>
public sealed class EmbeddingDimensionMismatchException : SchemaInitializationException
{
    /// <summary>The dimensionality the configured embedder produces (from <c>Neo4jOptions.EmbeddingDimensions</c>).</summary>
    public int ExpectedDimensions { get; }

    /// <summary>The vector indexes whose dimensionality does not match <see cref="ExpectedDimensions"/>.</summary>
    public IReadOnlyList<VectorIndexDimension> Mismatches { get; }

    /// <summary>Initializes a new instance describing every offending index.</summary>
    public EmbeddingDimensionMismatchException(
        int expectedDimensions,
        IReadOnlyList<VectorIndexDimension> mismatches)
        : base(BuildMessage(expectedDimensions, mismatches))
    {
        ExpectedDimensions = expectedDimensions;
        Mismatches = mismatches;
    }

    private static string BuildMessage(int expected, IReadOnlyList<VectorIndexDimension> mismatches)
    {
        var offenders = string.Join(", ",
            mismatches.Select(m => $"{m.IndexName} ({m.ActualDimensions})"));
        return
            $"Embedding dimension mismatch: configured EmbeddingDimensions is {expected}, but {mismatches.Count} " +
            $"existing vector index(es) were created with a different dimensionality: {offenders}. " +
            "Drop and recreate the offending index(es) at the configured dimensionality, or set " +
            "Neo4jOptions.EmbeddingDimensions to match your embedding model. " +
            "(Set Neo4jOptions.ValidateVectorIndexDimensions = false to disable this check.)";
    }
}
