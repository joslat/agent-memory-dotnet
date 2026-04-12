namespace Neo4j.AgentMemory.Abstractions.Options;

/// <summary>
/// Mode for blending memory and GraphRAG retrieval.
/// </summary>
public enum RetrievalBlendMode
{
    /// <summary>
    /// Memory only.
    /// </summary>
    MemoryOnly,

    /// <summary>
    /// GraphRAG only.
    /// </summary>
    GraphRagOnly,

    /// <summary>
    /// Memory first, then GraphRAG.
    /// </summary>
    MemoryThenGraphRag,

    /// <summary>
    /// GraphRAG first, then memory.
    /// </summary>
    GraphRagThenMemory,

    /// <summary>
    /// Blended memory and GraphRAG.
    /// </summary>
    Blended
}
