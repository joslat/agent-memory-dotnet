namespace AgentMemory.Abstractions.Options;

/// <summary>
/// Configuration for memory recall operations.
/// </summary>
public sealed record RecallOptions
{
    /// <summary>
    /// How long recall may spend assembling context before returning what it has (rank 13).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see langword="null"/> — the default — waits for every section, which is exactly today's
    /// behaviour. When set, sections still running when the budget expires are dropped and the
    /// context comes back without them.
    /// </para>
    /// <para>
    /// <b>Degradation is marked, never silent.</b> Every dropped section reports
    /// <c>TimedOut</c> in its diagnostics, and <c>RecallResult.Metadata["latencyBudgetExceeded"]</c>
    /// is set. A section cut short otherwise looks identical to one that searched and found nothing,
    /// and a caller answering from a context it believes is complete is worse off than one that
    /// waited.
    /// </para>
    /// <para>
    /// Abandoned work is not cancelled mid-flight — the queries are already in the driver, and
    /// tearing them down buys nothing the caller is waiting for. Their results are simply not used.
    /// </para>
    /// </remarks>
    public TimeSpan? LatencyBudget { get; init; }

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

    /// <summary>
    /// Per-category similarity floor for <b>reasoning traces</b>. Null (the default) uses
    /// <see cref="MinSimilarityScore"/>, which is today's behaviour exactly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why traces need their own floor.</b> At the shared 0.7 default, procedure retrieval
    /// <i>never abstains</i>: a sweep found every threshold from 0.00 to 0.86 behaves identically —
    /// a dead zone — and the measured knee is <b>0.92</b>. So the one setting that looks like it
    /// controls procedure precision controls nothing across the whole range anyone would plausibly
    /// set it to.
    /// </para>
    /// <para>
    /// <b>Why this is a safety property, not a tuning knob.</b> An agent handed a confident wrong
    /// procedure <i>executes</i> it, where an agent handed nothing investigates. Recalling no
    /// procedure is a strictly better failure than recalling the wrong one, and at the shared default
    /// the second outcome is the only one available.
    /// </para>
    /// <para>
    /// <b>Recommended value: 0.92</b> (the measured knee); 0.90 is the free variant, at which no
    /// correct answer was lost in the sweep. Left null by default because raising it changes what
    /// recall returns, and every sealed measurement was taken at the shared floor.
    /// </para>
    /// </remarks>
    public double? MinTraceSimilarityScore { get; init; }

    /// <summary>
    /// The floor traces are actually retrieved at: <see cref="MinTraceSimilarityScore"/> when set,
    /// otherwise <see cref="MinSimilarityScore"/>.
    /// </summary>
    /// <remarks>
    /// Resolved here rather than at each call site so the two recall paths cannot disagree — the exact
    /// way <c>SuccessfulTracesOnly</c> came to be passed live and hardcoded null on the as-of path.
    /// </remarks>
    public double EffectiveTraceMinScore => MinTraceSimilarityScore ?? MinSimilarityScore;

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

    /// <summary>
    /// Whether live recall honours a fact's valid-time window. Defaults to
    /// <see cref="ValidTimeMode.Ignore"/> — today's behaviour.
    /// </summary>
    /// <remarks>
    /// <c>MemoryProfile.Parity</c> must resolve to <see cref="ValidTimeMode.Ignore"/>: parity means
    /// "ranks exactly like upstream Python", and upstream has no valid-time gate on its live path.
    /// </remarks>
    public ValidTimeMode ValidTime { get; init; } = ValidTimeMode.Ignore;

    /// <summary>
    /// Surfaces facts that <b>became due</b> since the last checkpoint, and facts about to expire,
    /// without being asked for them. Default off, and off is byte-identical.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything else in recall is <i>reactive</i>: it answers the question in front of it. A reminder
    /// is by definition off-topic — nobody asks "is there anything I should know?" — so a
    /// similarity-scored channel can never surface one. This is a <b>time-predicate</b> selection with
    /// no embedding and no similarity floor, and that absence is the specification rather than an
    /// optimisation.
    /// </para>
    /// <para>
    /// Only evaluated when <see cref="ValidTime"/> is <see cref="ValidTimeMode.Current"/>: firing reads
    /// a fact's valid-time window, and a store that is ignoring valid time has no window to read.
    /// <c>MemoryProfile.Parity</c> resolves this to <see langword="false"/>, because upstream has no
    /// firing and parity means ranking exactly like upstream.
    /// </para>
    /// </remarks>
    public bool ProspectiveFiring { get; init; }

    /// <summary>
    /// Budget for the DUE section — its own claimant, never competing with <see cref="MaxFacts"/>.
    /// </summary>
    /// <remarks>
    /// A separate budget because a volunteered reminder that loses a budget contest to a
    /// relevance-ranked fact has failed at precisely the thing it exists to do.
    /// </remarks>
    public int MaxDueItems { get; init; } = 5;

    /// <summary>A fact whose <c>valid_until</c> falls within this window of now renders as EXPIRING.</summary>
    public TimeSpan ExpiringWindow { get; init; } = TimeSpan.FromDays(7);

    /// <summary>
    /// How far back to look for newly-due facts when no delta checkpoint is available.
    /// </summary>
    /// <remarks>
    /// Also the <b>clamp</b> on a checkpoint that is available: a caller returning after months away
    /// would otherwise flood the DUE section with everything that became true in the interim. Bounded
    /// three ways — this clamp, <see cref="MaxDueItems"/>, and visible truncation in section
    /// diagnostics.
    /// </remarks>
    public TimeSpan DueLookback { get; init; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Reports aged-out memory as a stated absence when a fact section comes back thin. Default off,
    /// and off is byte-identical.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Forgetting already works and is <b>invisible</b>: decay prunes, recall returns less, and the
    /// agent answers as though it had never known — indistinguishable, to the person asking, from never
    /// having been told. This makes the gap sayable.
    /// </para>
    /// <para>
    /// The probe runs <b>only on thin recalls</b> and only when a query embedding exists, so a
    /// well-answered turn pays nothing. What surfaces is a summary — topic, count, dates — never the
    /// forgotten content itself, because rendering that would undo the forgetting.
    /// </para>
    /// </remarks>
    public bool LegibleForgetting { get; init; }

    /// <summary>Candidate cap for the decayed-fact probe.</summary>
    public int TombstoneProbeTopK { get; init; } = 10;

    /// <summary>
    /// Which projection features render what the store knows but the renderers discard. All off by
    /// default, and off is byte-identical.
    /// </summary>
    /// <remarks>
    /// Left at <see cref="MemoryProjectionOptions.Default"/>, a request inherits the application-level
    /// value configured on <c>MemoryOptions</c> — the same reference-equality inheritance
    /// <c>MemoryOptions.Recall</c> uses, so "the caller did not ask" stays distinguishable from "the
    /// caller asked for the defaults".
    /// </remarks>
    public MemoryProjectionOptions Projection { get; init; } = MemoryProjectionOptions.Default;

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
    /// It is now honoured on both recall paths: live and point-in-time (<c>AssembleContextAsOfAsync</c>,
    /// which forwarded a hardcoded <c>null</c> of its own for longer than the live path did).
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
