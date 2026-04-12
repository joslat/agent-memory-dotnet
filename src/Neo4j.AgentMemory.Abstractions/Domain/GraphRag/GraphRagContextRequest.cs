namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Request for GraphRAG context retrieval.
/// </summary>
public sealed record GraphRagContextRequest
{
    /// <summary>
    /// Session identifier.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Optional user identifier.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>
    /// User query.
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// Maximum number of results.
    /// </summary>
    public int TopK { get; init; } = 5;

    /// <summary>
    /// Search mode.
    /// </summary>
    public GraphRagSearchMode SearchMode { get; init; } = GraphRagSearchMode.Hybrid;

    /// <summary>
    /// Additional options.
    /// </summary>
    public IReadOnlyDictionary<string, object> Options { get; init; } =
        new Dictionary<string, object>();
}
