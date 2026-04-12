namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Result from GraphRAG context retrieval.
/// </summary>
public sealed record GraphRagContextResult
{
    /// <summary>
    /// Retrieved context items.
    /// </summary>
    public required IReadOnlyList<GraphRagContextItem> Items { get; init; }

    /// <summary>
    /// Retrieval metadata.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } =
        new Dictionary<string, object>();
}
