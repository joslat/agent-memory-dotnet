namespace Neo4j.AgentMemory.Neo4j.Infrastructure;

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
}
