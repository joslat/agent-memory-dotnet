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

    /// <summary>
    /// Falls back to an owner-bounded similarity scan when an owner-scoped vector search returns
    /// fewer rows than were asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Neo4j's vector index is global, so an owner filter is a POST-filter on a top-K drawn from every
    /// tenant. Measured on a 50-owner corpus, a mean of <b>7 of 60</b> candidates reached the querying
    /// owner. Today only a <i>totally empty</i> result triggers a rescue; a search returning 2 rows of
    /// a requested 10 is accepted as the answer.
    /// </para>
    /// <para>
    /// That is sometimes right and sometimes badly wrong: question <c>5d3d2817</c> returned 2 facts
    /// from a 710-fact graph with the answer present, and was answered incorrectly in both arms.
    /// </para>
    /// <para>
    /// Off by default. It costs one extra query per short result — bounded by the owner's own rows,
    /// not the corpus — and every recorded measurement was taken without it, so turning it on is a
    /// stated decision rather than an inherited one.
    /// </para>
    /// </remarks>
    public bool RescueShortOwnerResults { get; init; }

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

    /// <summary>
    /// Multi-tenant isolation policy configuration. Defaults to
    /// <see cref="MemoryIsolationMode.SingleTenant"/> (today's backward-compatible behavior); see
    /// <c>docs/getting-started.md</c> "Owner isolation" before enabling a stricter mode.
    /// </summary>
    public MemoryIsolationOptions Isolation { get; init; } = new();
}
