using AgentMemory.Abstractions.Options;

namespace AgentMemory.Abstractions.Domain;

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
    /// The blend mode that produced this context. Determines which sources were retrieved
    /// (see <see cref="RetrievalBlendMode"/>) and the order in which memory and GraphRAG-derived
    /// context are rendered by formatters. Defaults to <see cref="RetrievalBlendMode.Blended"/>.
    /// </summary>
    public RetrievalBlendMode BlendMode { get; init; } = RetrievalBlendMode.Blended;

    /// <summary>
    /// UTC timestamp when the context was assembled.
    /// </summary>
    public required DateTimeOffset AssembledAtUtc { get; init; }

    /// <summary>
    /// True when the configured context budget forced items (or the GraphRAG block) to be dropped while
    /// assembling this context. False when everything fit within the budget (or no budget was configured).
    /// Surfaced to callers via <c>RecallResult.Truncated</c>.
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } =
        new Dictionary<string, object>();
}
