namespace AgentMemory.Abstractions.Options;

/// <summary>
/// Root configuration for the memory system.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scalar options are settable; nested option objects are <c>init</c>-only. That split is
/// deliberate (25.1).</b>
/// </para>
/// <para>
/// Every scalar here used to be <c>init</c>-only, which made the idiomatic
/// <c>Action&lt;MemoryOptions&gt;</c> registration overload unable to configure a single flag — a
/// consumer following the standard .NET options pattern could reach exactly one of twenty option
/// groups (<see cref="Extraction"/>, the only mutable one). Widening <c>init</c> to <c>set</c> is both
/// source- and binary-compatible, so object initialisers keep working and the configure lambda starts
/// working.
/// </para>
/// <para>
/// <b>The nested objects deliberately did not get the same treatment.</b> Several of them default to a
/// shared static singleton — <c>RecallOptions.Default</c>, <c>ContextBudget.Default</c>,
/// <c>MemoryDecayOptions.Default</c>, <c>MemoryRankingOptions.Default</c> are one instance each for the
/// whole process. If their properties were settable, <c>options.Recall.MaxFacts = 5</c> inside a
/// configure lambda would silently mutate the default for every other consumer in the application,
/// including ones registered later. Assign a fresh instance instead:
/// <c>options.Recall = RecallOptions.Default with { MaxFacts = 5 }</c> via an object initialiser, or
/// use the <c>MemoryOptions</c>-instance registration overload.
/// </para>
/// </remarks>
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
    public bool EnableGraphRag { get; set; }

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
    public bool RescueShortOwnerResults { get; set; }

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
    public bool NodeDistanceReranking { get; set; }

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
    public bool MentionFrequencyReranking { get; set; }

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
    public bool DeferAccessTracking { get; set; }

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
    public double ConfidenceReinforcementAlpha { get; set; }

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
    public bool ResolveTemporalQueries { get; set; }

    /// <summary>
    /// Which clocks a resolved temporal query binds. Defaults to
    /// <see cref="TemporalQueryClocks.ValidTimeOnly"/>; only consulted when
    /// <see cref="ResolveTemporalQueries"/> is enabled.
    /// </summary>
    /// <remarks>
    /// <b>Binding the transaction clock by default was a silent-empty-recall bug on a whole class of
    /// host.</b> <c>created_at</c> is ingestion time wherever history was imported, migrated or
    /// backfilled, so an as-of recall at any past instant excludes the entire store and returns an
    /// empty context with no error. See <see cref="TemporalQueryClocks"/> for why the two failure modes
    /// are not symmetric.
    /// </remarks>
    public TemporalQueryClocks TemporalQueryClocks { get; set; } = TemporalQueryClocks.ValidTimeOnly;

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
    public bool OmitEmbeddingsFromRecall { get; set; }

    /// <summary>
    /// Skips the escalation ladder for an owner that holds no rows of the searched label (2.13).
    /// </summary>
    /// <remarks>
    /// <para>
    /// An empty owner-scoped vector search escalates: a widened probe over the <b>global</b> index,
    /// then an owner-scoped scan. For an owner holding nothing of that label both are futile by
    /// construction, and the widened probe is the costly one — it asks the index for up to 2,000
    /// candidates across the whole corpus to find rows that do not exist.
    /// </para>
    /// <para>
    /// <b>Measured before enabling, because shortening the ladder is a trade rather than a free win.</b>
    /// A <i>starved</i> owner — many rows, crowded out of the global top-K by noisier neighbours — is
    /// genuinely recovered by escalation, so a blanket removal loses real answers. The large-owner arm
    /// showed the two populations are cleanly separable: the empty owner returns nothing at every
    /// rung, while the starved owner's rows are found. This option skips the ladder only for the first.
    /// </para>
    /// <para>
    /// <b>Off by default, and MEASURED to be the right default (2026-08-14).</b> Flipping it on and
    /// re-running the hermetic profile moved <c>PERF-R-01</c> from <b>13 queries to 16</b> — worse,
    /// not better. The probe is an <i>additional</i> query per category, and it only pays for itself
    /// when the owner turns out to be empty. On a turn where the owner does hold rows, all it buys is
    /// three probes that answer "yes, look anyway".
    /// </para>
    /// <para>
    /// So this is a bet on the shape of the workload, not a free saving: enable it for a deployment
    /// dominated by owners with little or no stored memory (a large multi-tenant estate with a long
    /// tail of near-empty tenants), and leave it off otherwise. The results are identical either way —
    /// an owner with nothing to find finds nothing, asserted live — so the trade is purely cost, in
    /// both directions.
    /// </para>
    /// <para>
    /// It is also gated because an existence probe that disagreed with the search's own scoping would
    /// skip a rescue that would have worked, and a silent recall loss is not worth one avoided query.
    /// </para>
    /// </remarks>
    public bool SkipEscalationWhenOwnerHasNoRows { get; set; }

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
