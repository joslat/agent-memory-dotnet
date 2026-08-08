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

    /// <summary>
    /// G5 "hard" tier. After the similarity-ranked facts are chosen, also returns every fact sharing
    /// their canonical predicates, so a relation arrives <b>whole</b>. Default off.
    /// </summary>
    /// <remarks>
    /// Top-K is a relevance cutoff and gives no completeness guarantee, so aggregation questions
    /// ("how many...", "list all...") cannot be answered from it: missing one of five matching facts
    /// silently yields four. Enable this when the question is an aggregation; it widens the context,
    /// so it is not the default.
    /// </remarks>
    public bool ExpandFactsByPredicate { get; init; }

    /// <summary>Cap on facts returned by predicate expansion. Unbounded completeness would exhaust the budget.</summary>
    public int MaxExpandedFacts { get; init; } = 100;

    /// <summary>
    /// Restricts recalled reasoning traces by outcome: <c>true</c> successful only, <c>false</c>
    /// failed only, <c>null</c> (default) no filter.
    /// </summary>
    /// <remarks>
    /// The repository and its Cypher have always supported this, and automatic recall passed a
    /// hardcoded <c>null</c>, so nothing could ever reach it — a built, plumbed, unreachable option.
    /// <para>
    /// It matters because a recalled trace is presented to the reader as precedent with nothing
    /// marking it as a failure, so imitating reasoning that did not work is worse than recalling
    /// nothing. Upstream <c>neo4j-labs/agent-memory</c> treats this as correctness rather than tuning
    /// and defaults its equivalent to successful-only. The default here stays at today's behaviour
    /// because nothing becomes a default before it is measured, and the trace surface has never been
    /// measured at all — it has carried a recall budget of zero in every quality run to date.
    /// </para>
    /// </remarks>
    public bool? SuccessfulTracesOnly { get; init; }

    /// <summary>
    /// Also expand on the relations the query text itself names, not only those the top-K surfaced.
    /// </summary>
    /// <remarks>
    /// Requires <see cref="ExpandFactsByPredicate"/>. Expansion makes one relation complete, but it can
    /// only widen predicates similarity already nominated, so a question naming several relations
    /// ("did I buy, assemble, sell, or fix...") reaches only whichever of them retrieval happened to
    /// surface. This resolves the question's own verbs instead. Off by default: it widens the context,
    /// and nothing is a default here until it has been measured.
    /// </remarks>
    public bool ResolveQueryRelations { get; init; }
}
