namespace AgentMemory.Abstractions.Options;

/// <summary>
/// Root configuration for the memory system.
/// </summary>
public sealed record MemoryOptions
{
    /// <summary>Short-term memory configuration.</summary>
    public ShortTermMemoryOptions ShortTerm { get; init; } = new();

    /// <summary>Long-term memory configuration.</summary>
    public LongTermMemoryOptions LongTerm { get; init; } = new();

    /// <summary>Reasoning memory configuration.</summary>
    public ReasoningMemoryOptions Reasoning { get; init; } = new();

    /// <summary>Recall configuration.</summary>
    public RecallOptions Recall { get; init; } = RecallOptions.Default;

    /// <summary>Context budget configuration.</summary>
    public ContextBudget ContextBudget { get; init; } = ContextBudget.Default;

    /// <summary>Whether to enable GraphRAG integration.</summary>
    public bool EnableGraphRag { get; init; }

    // NOTE: extraction at the Core layer is explicit (call ExtractAndPersistAsync /
    // ExtractFromSessionAsync). Automatic extraction on message persist is an adapter concern, configured
    // by AgentFrameworkOptions.AutoExtractOnPersist. The former EnableAutoExtraction flag here was read
    // nowhere (Core AddMessageAsync never auto-extracted), so it was removed.

    /// <summary>Extraction pipeline configuration.</summary>
    public ExtractionOptions Extraction { get; init; } = new();

    /// <summary>Memory decay and forgetting configuration.</summary>
    public MemoryDecayOptions MemoryDecay { get; init; } = MemoryDecayOptions.Default;

    /// <summary>
    /// Retrieval-ranking configuration (recency / structural re-ranking). Opt-in and schema-neutral;
    /// defaults to <see cref="MemoryProfile.Parity"/> (semantic-only ranking — today's behaviour).
    /// </summary>
    public MemoryRankingOptions Ranking { get; init; } = MemoryRankingOptions.Default;
}
