namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Represents the assembled memory context for an agent run.
/// </summary>
public sealed record MemoryContext
{
    /// <summary>
    /// Session identifier for this context.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Recent conversation messages.
    /// </summary>
    public MemoryContextSection<Message> RecentMessages { get; init; } =
        MemoryContextSection<Message>.Empty;

    /// <summary>
    /// Semantically relevant past messages.
    /// </summary>
    public MemoryContextSection<Message> RelevantMessages { get; init; } =
        MemoryContextSection<Message>.Empty;

    /// <summary>
    /// Relevant entities.
    /// </summary>
    public MemoryContextSection<Entity> RelevantEntities { get; init; } =
        MemoryContextSection<Entity>.Empty;

    /// <summary>
    /// Relevant preferences.
    /// </summary>
    public MemoryContextSection<Preference> RelevantPreferences { get; init; } =
        MemoryContextSection<Preference>.Empty;

    /// <summary>
    /// Relevant facts.
    /// </summary>
    public MemoryContextSection<Fact> RelevantFacts { get; init; } =
        MemoryContextSection<Fact>.Empty;

    /// <summary>
    /// Similar past reasoning traces.
    /// </summary>
    public MemoryContextSection<ReasoningTrace> SimilarTraces { get; init; } =
        MemoryContextSection<ReasoningTrace>.Empty;

    /// <summary>
    /// Optional GraphRAG-derived context.
    /// </summary>
    public string? GraphRagContext { get; init; }

    /// <summary>
    /// UTC timestamp when the context was assembled.
    /// </summary>
    public required DateTimeOffset AssembledAtUtc { get; init; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } =
        new Dictionary<string, object>();
}
