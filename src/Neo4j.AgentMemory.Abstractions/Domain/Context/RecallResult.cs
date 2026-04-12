namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Result of a memory recall operation.
/// </summary>
public sealed record RecallResult
{
    /// <summary>
    /// Assembled memory context.
    /// </summary>
    public required MemoryContext Context { get; init; }

    /// <summary>
    /// Total number of items retrieved across all sections.
    /// </summary>
    public int TotalItemsRetrieved { get; init; }

    /// <summary>
    /// Whether the context was truncated due to budget limits.
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>
    /// Estimated token count of the assembled context.
    /// </summary>
    public int? EstimatedTokenCount { get; init; }

    /// <summary>
    /// Additional metadata about the recall operation.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } =
        new Dictionary<string, object>();
}
