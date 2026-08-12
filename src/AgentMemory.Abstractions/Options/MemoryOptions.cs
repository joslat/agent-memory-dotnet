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

    /// <summary>
    /// Boosts recalled facts that sit close, in the graph, to the entity the query is about (R6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Vector similarity asks whether a row <b>looks like</b> the query; this asks whether it is
    /// <b>about</b> what the query is about. A plainly-worded fact hanging directly off the named
    /// entity is often the answer, while a fact that paraphrases the query beautifully may concern
    /// someone else entirely.
    /// </para>
    /// <para>
    /// Reuses <c>MemoryRankingOptions.StructuralDecayGamma</c> as its decay constant rather than
    /// introducing a second one, and does nothing unless that decay is also enabled -- at gamma = 1
    /// every boost is 1.0, so the two extra queries could not change an ordering.
    /// </para>
    /// <para>
    /// Off by default: it changes recall ordering, costs two bounded queries per fact section, and
    /// every recorded measurement was taken without it.
    /// </para>
    /// </remarks>
    public bool NodeDistanceReranking { get; init; }

    /// <summary>
    /// Boosts recalled facts the conversation keeps returning to (R7).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A fact the world asserted five times is usually more central than one mentioned once, and
    /// similarity cannot see that: two facts phrased alike score alike however often either was said.
    /// </para>
    /// <para>
    /// <b>Ingestion-side.</b> It counts how often the WORLD re-asserted a fact, not how often we
    /// retrieved it. Ranking on our own retrievals -- the <c>:MemoryReadAudit</c> trail -- would be a
    /// rich-get-richer loop: whatever ranks highly gets retrieved, which raises its count, which
    /// raises its rank. That looks like learning and is self-reinforcement.
    /// </para>
    /// <para>
    /// Logarithmic, so the gap between one mention and three counts while the gap between thirty and
    /// thirty-two does not. Off by default; every recorded measurement was taken without it.
    /// </para>
    /// </remarks>
    public bool MentionFrequencyReranking { get; init; }

    /// <summary>
    /// Starts the post-recall access-tracking write without waiting for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Access tracking is bookkeeping — it feeds decay and retention, and nothing in the recall the
    /// caller is waiting for depends on it. Awaiting it puts a write burst on the pre-model read path:
    /// at shipped defaults, up to 25 nodes updated before the model is invoked.
    /// </para>
    /// <para>
    /// <b>Off by default, and the reason is a hazard rather than caution.</b> The write runs on scoped
    /// services — a driver session, a repository — and a host that disposes its DI scope when the
    /// response is returned will dispose them out from under a deferred write. That surfaces as an
    /// <c>ObjectDisposedException</c> in a log nobody reads, and access tracking silently stops. Hosts
    /// whose scope outlives the response (a long-lived agent, a hosted service) can turn it on; a
    /// short-lived request scope should not.
    /// </para>
    /// <para>
    /// Deferred work is detached from the request's cancellation token deliberately: that token is
    /// cancelled as soon as the response completes, so passing it through would cancel the very write
    /// that was deferred — a feature that looks enabled and does nothing.
    /// </para>
    /// </remarks>
    public bool DeferAccessTracking { get; init; }

    /// <summary>
    /// How much a fact's confidence moves when the world corroborates or contradicts it (S2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Today confidence is set once by extraction and never earns or loses anything: a fact stated
    /// five times and one stated in passing carry whatever number the extractor happened to report.
    /// With α &gt; 0 a re-asserted triple gains α, and a fact superseded by a contradiction loses 2α.
    /// </para>
    /// <para>
    /// <b>Asymmetric on purpose.</b> Being contradicted is stronger evidence against a fact than one
    /// more restatement is for it — a claim that is simply repeated may just be a habit of phrasing,
    /// while a claim the world has replaced is one the world stopped believing.
    /// </para>
    /// <para>
    /// Clamped to [0,1] at both ends. Confidence is read by ranking, dedup and decay, and a value
    /// outside that range would propagate into computations where it means nothing.
    /// </para>
    /// <para>
    /// <b>0 (off) by default, and it is the off switch in Cypher too</b>: at α = 0 the upsert's
    /// confidence assignment is byte-for-byte what it was, so no sealed measurement moves. A sensible
    /// starting value is 0.02–0.05 — large enough to separate a well-attested fact over a
    /// conversation, small enough that a single restatement does not overwhelm what extraction judged.
    /// </para>
    /// </remarks>
    public double ConfidenceReinforcementAlpha { get; init; }

    /// <summary>
    /// Routes a turn that names a past time to bitemporal recall at that time (R4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>RecallAsOfAsync</c> has existed since the bitemporal work, and until now nothing in an
    /// ordinary conversation could reach it: "what did I think back in March?" recalled against now,
    /// exactly like every other question. Without this, the bitemporal read path is a capability
    /// nothing can ask for.
    /// </para>
    /// <para>
    /// Detection is deterministic, needs no model call, and errs heavily toward <b>not</b> matching:
    /// a missed expression costs nothing (the turn recalls against now, today's behaviour), while a
    /// false positive silently narrows recall to a window the user never asked about and returns an
    /// answer that looks entirely ordinary. So "in March" resolves and a bare "March" does not;
    /// "last week" resolves and "the last item" does not.
    /// </para>
    /// <para>
    /// Off by default: it changes which memories a temporal question sees.
    /// </para>
    /// <para>
    /// <b>Scope.</b> Honoured by <c>IMemoryService.RecallAsync</c>, and therefore by everything that
    /// recalls through it — the MCP memory tools, the Agent Framework and Semantic Kernel adapters.
    /// The <c>memory://context/{session_id}</c> MCP <i>resource</i> is the one read surface that does
    /// not: it composes <c>IMemoryContextAssembler</c> directly, and the MCP server references only
    /// AgentMemory.Abstractions by design. Reaching the resolver from there would mean a project
    /// reference to AgentMemory.Core purely for a secondary surface, so the divergence is recorded
    /// here rather than papered over — a temporal question asked through that resource recalls against
    /// now.
    /// </para>
    /// </remarks>
    public bool ResolveTemporalQueries { get; init; }

    /// <summary>
    /// Stops vector recall shipping the stored embedding back with every hit (rank 13 / payload).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every entity, fact and preference vector search returns the whole node — including its
    /// embedding, which at 384 dimensions is roughly <b>3 KB per item and ~130 KB per turn</b> moved
    /// across the wire, deserialized, and thrown away. Nothing on the recall path reads it: the
    /// similarity was computed inside the index.
    /// </para>
    /// <para>
    /// A measured prototype showed <b>−91% on the entity transaction and −21% on the whole turn</b>
    /// with every quality guard flat.
    /// </para>
    /// <para>
    /// <b>Opt-in, and that is a correctness decision rather than caution.</b> Recalled memories come
    /// back with <c>Embedding = null</c> when this is on. Anything that re-uses a recalled vector —
    /// notably the TCK conformance bridge, which serialises embeddings on three search endpoints —
    /// must leave it off. Making it a setting the caller enables means such a consumer is unaffected
    /// <i>by construction</i>, rather than by remembering to check.
    /// </para>
    /// <para>
    /// Honoured by the entity, fact and preference vector searches, which share one query finisher.
    /// Message and trace searches keep returning whole nodes.
    /// </para>
    /// </remarks>
    public bool OmitEmbeddingsFromRecall { get; init; }

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
