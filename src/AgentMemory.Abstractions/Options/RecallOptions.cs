namespace AgentMemory.Abstractions.Options;

/// <summary>
/// Configuration for memory recall operations.
/// </summary>
public sealed record RecallOptions
{
    /// <summary>Maximum recent messages to include.</summary>
    public int MaxRecentMessages { get; init; } = 10;

    /// <summary>Maximum semantically relevant messages to include.</summary>
    public int MaxRelevantMessages { get; init; } = 5;

    /// <summary>Maximum entities to include.</summary>
    public int MaxEntities { get; init; } = 10;

    /// <summary>Maximum preferences to include.</summary>
    public int MaxPreferences { get; init; } = 5;

    /// <summary>Maximum facts to include.</summary>
    public int MaxFacts { get; init; } = 10;

    /// <summary>Maximum reasoning traces to include.</summary>
    public int MaxTraces { get; init; } = 3;

    /// <summary>Maximum GraphRAG items to include.</summary>
    public int MaxGraphRagItems { get; init; } = 5;

    /// <summary>Minimum similarity score for semantic search (0.0 to 1.0).</summary>
    public double MinSimilarityScore { get; init; } = 0.7;

    /// <summary>Retrieval blend mode.</summary>
    public RetrievalBlendMode BlendMode { get; init; } = RetrievalBlendMode.Blended;

    /// <summary>
    /// Optional owner/user scope for long-term recall. When null, the assembler derives a scope from
    /// <c>RecallRequest.UserId</c> (or recalls globally if that is also null). See <c>MemoryScope</c>.
    /// </summary>
    public MemoryScope? Scope { get; init; }

    /// <summary>
    /// Per-request ranking intent (D3): <see cref="RankingIntent.Latest"/> favours fresh memories,
    /// <see cref="RankingIntent.Analog"/> favours structurally/semantically similar memories regardless of
    /// age (precedent retrieval). Applied over the configured <see cref="MemoryRankingOptions"/> for this
    /// recall only. Default ⇒ the configured weights unchanged.
    /// </summary>
    public RankingIntent Intent { get; init; } = RankingIntent.Default;

    /// <summary>
    /// Includes ranked retrieval diagnostics in returned memory-context sections when the selected
    /// provider supports them. Disabled by default so ordinary recalls retain their current payload
    /// and allocation profile.
    /// </summary>
    public bool IncludeDiagnostics { get; init; }

    /// <summary>Default singleton instance.</summary>
    public static RecallOptions Default { get; } = new();
}
