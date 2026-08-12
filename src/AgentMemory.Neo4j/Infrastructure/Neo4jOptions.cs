namespace AgentMemory.Neo4j.Infrastructure;

public class Neo4jOptions
{
    public string Uri { get; set; } = "bolt://localhost:7687";
    public string Username { get; set; } = "neo4j";
    public string Password { get; set; } = "password";
    public string Database { get; set; } = "neo4j";
    public int MaxConnectionPoolSize { get; set; } = 100;
    public TimeSpan ConnectionAcquisitionTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public bool EncryptionEnabled { get; set; } = false;

    /// <summary>
    /// Server-side deadline for every managed read/write transaction. <see langword="null"/> (default)
    /// means no deadline, which is today's behaviour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing bounds a query today. The driver's <c>ExecuteReadAsync</c>/<c>ExecuteWriteAsync</c> take
    /// no <see cref="CancellationToken"/>, so an in-flight query cannot be interrupted from this side —
    /// cooperative cancellation only checks <em>before</em> work starts. A transaction timeout is the
    /// only mechanism that actually bounds one, because the <b>server</b> enforces it.
    /// </para>
    /// <para>
    /// This matters more since the owner-starvation rescue: a scoped search that returns nothing falls
    /// back to a scan bounded by one owner's data, and "one owner's data" is unbounded in principle.
    /// </para>
    /// <para>
    /// <b>Default is null on purpose.</b> A timeout that is too low converts a slow query into a failed
    /// one, and the right value depends on deployment shape, so this is a deliberate operator decision
    /// rather than a number chosen here. Values must be strictly positive; zero or negative is rejected
    /// at startup rather than silently meaning "no timeout".
    /// </para>
    /// </remarks>
    public TimeSpan? TransactionTimeout { get; set; }

    /// <summary>
    /// Dimensionality of embedding vectors. Must match the model used to generate embeddings.
    /// Default is 1536 (OpenAI text-embedding-ada-002 / text-embedding-3-small).
    /// </summary>
    public int EmbeddingDimensions { get; set; } = 1536;

    /// <summary>
    /// Persists a message batch, its embeddings, ordering links, and read-back in one Cypher query.
    /// Enabled by default; disable only for compatibility diagnosis or controlled A/B measurement.
    /// The legacy path remains available and preserves its original multi-query behavior.
    /// </summary>
    public bool UseOptimizedMessageBatchWrites { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/> (the default), schema bootstrap verifies that every existing vector
    /// index was created with <see cref="EmbeddingDimensions"/> and throws
    /// <see cref="AgentMemory.Abstractions.Exceptions.EmbeddingDimensionMismatchException"/> if any
    /// differ. This is a fail-fast guard against switching embedding models without recreating the
    /// indexes (which would otherwise surface as an opaque query-time error). Set to
    /// <see langword="false"/> to skip the check (e.g. during an intentional, staged migration).
    /// </summary>
    public bool ValidateVectorIndexDimensions { get; set; } = true;
}
