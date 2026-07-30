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
