namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// A single context item from GraphRAG.
/// </summary>
public sealed record GraphRagContextItem
{
    /// <summary>
    /// Context text.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Relevance score.
    /// </summary>
    public double Score { get; init; }

    /// <summary>
    /// Source node identifiers.
    /// </summary>
    public IReadOnlyList<string> SourceNodeIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Item metadata.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } =
        new Dictionary<string, object>();
}
