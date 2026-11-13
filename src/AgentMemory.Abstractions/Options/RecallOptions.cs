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
    /// <see langword="null"/> â€” the default â€” waits for every section, which is exactly today's
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
    /// Abandoned work is not cancelled mid-flight â€” the queries are already in the driver, and
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

    /// <summary>
    /// 36.7. Maximum relationships to include: the live relationships touching the recalled entities, rendered as
    /// "Rosa, best friend, Carmen". 0 (the default) recalls none, as before.
    /// </summary>
    /// <remarks>
    /// Relationships had no section in the recalled context at all, so "Carmen is my best friend", stored as a
    /// relationship, never reached the agent (found in simulated conversations: "is she a colleague or a friend?").
    /// Live recall only; the point-in-time path does not read relationships.
    /// </remarks>
    public int MaxRelationships { get; init; }

    /// <summary>Maximum preferences to include.</summary>
    public int MaxPreferences { get; init; } = 5;

    /// <summary>Maximum facts to include.</summary>
    public int MaxFacts { get; init; } = 10;

    /// <summary>
    /// How derived facts (the session accountant's counts and sums) are treated at recall.
    /// <c>null</c> â€” the default â€” is today's behaviour: they compete for the ordinary fact budget.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three deliberate states, and null is the measured-harmful one.</b>
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>null</c> â€” derived facts sit in the same similarity pool as everything else and consume
    /// <see cref="MaxFacts"/> slots. **Measured cost: enabling the accountant on the arithmetic
    /// vertical moved 30% â†’ 14%**, because ~2,560 derived facts displaced the source values they
    /// were derived from and facts-per-question FELL from 35.4 to 27.6. This remains the default
    /// only because changing it would alter every sealed measurement taken under it.
    /// </description></item>
    /// <item><description>
    /// <c>0</c> â€” derived facts are excluded from recall. Strictly better than <c>null</c> whenever
    /// the accountant is enabled: an unread derived fact is pure dilution.
    /// </description></item>
    /// <item><description>
    /// <c>&gt; 0</c> â€” derived facts get their OWN budget, retrieved separately and appended, so a
    /// count or sum can reach the prompt <i>without</i> costing a source fact its slot. This is the
    /// state in which the accountant can finally be shown to help or not.
    /// </description></item>
    /// </list>
    /// <para>
    /// Derived facts are identified by <c>derivation_key</c>, which the derived-fact upsert writes
    /// and no ordinary fact carries â€” so the filter is a property test, not a heuristic.
    /// </para>
    /// </remarks>
    public int? MaxDerivedFacts { get; init; }

    /// <summary>Maximum reasoning traces to include.</summary>
    public int MaxTraces { get; init; } = 3;

    /// <summary>Maximum GraphRAG items to include.</summary>
    public int MaxGraphRagItems { get; init; } = 5;

    /// <summary>Minimum similarity score for semantic search (0.0 to 1.0).</summary>
    /// <remarks>
    /// On the store's scale: Neo4j's vector search scores <c>(1 + cosine) / 2</c>, in [0, 1], so 0.7 is a cosine of 0.40 and 0.55 a cosine of 0.10. A threshold remembered as a cosine is far stricter than the same number here.
    /// </remarks>
    public double MinSimilarityScore { get; init; } = 0.7;

    /// <summary>
    /// Per-category similarity floor for <b>reasoning traces</b>. Null (the default) uses
    /// <see cref="MinSimilarityScore"/>, which is today's behaviour exactly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why traces need their own floor.</b> At the shared 0.7 default, procedure retrieval
    /// <i>never abstains</i>: a sweep found every threshold from 0.00 to 0.86 behaves identically â€”
    /// a dead zone â€” and the measured knee is <b>0.92</b>. So the one setting that looks like it
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
    /// Resolved here rather than at each call site so the two recall paths cannot disagree â€” the exact
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
    /// recall only. Default â‡’ the configured weights unchanged.
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
    /// <see cref="ValidTimeMode.Ignore"/> â€” today's behaviour.
    /// </summary>
    /// <remarks>
    /// The default, <see cref="ValidTimeMode.Ignore"/>, is also what parity with upstream Python means: upstream
    /// has no valid-time gate on its live path. Nothing derives this value from <c>MemoryProfile</c>; set it here.
    /// </remarks>
    public ValidTimeMode ValidTime { get; init; } = ValidTimeMode.Ignore;

    /// <summary>
    /// Surfaces facts that <b>became due</b> since the last checkpoint, and facts about to expire,
    /// without being asked for them. Default off, and off is byte-identical.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything else in recall is <i>reactive</i>: it answers the question in front of it. A reminder
    /// is by definition off-topic â€” nobody asks "is there anything I should know?" â€” so a
    /// similarity-scored channel can never surface one. This is a <b>time-predicate</b> selection with
    /// no embedding and no similarity floor, and that absence is the specification rather than an
    /// optimisation.
    /// </para>
    /// <para>
    /// Only evaluated when <see cref="ValidTime"/> is <see cref="ValidTimeMode.Current"/>: firing reads
    /// a fact's valid-time window, and a store that is ignoring valid time has no window to read. Configured
    /// through <c>MemoryOptions.Recall</c>, setting this without <see cref="ValidTimeMode.Current"/> logs one
    /// warning and live recall does not fire; a per-request <see cref="RecallOptions"/> with the same mismatch
    /// simply does not fire. As-of recall fires without this gate. The default is off, which is also what parity with upstream means (upstream
    /// has no firing); nothing derives it from <c>MemoryProfile</c>.
    /// </para>
    /// </remarks>
    public bool ProspectiveFiring { get; init; }

    /// <summary>
    /// Budget for the DUE section â€” its own claimant, never competing with <see cref="MaxFacts"/>.
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
    /// three ways â€” this clamp, <see cref="MaxDueItems"/>, and visible truncation in section
    /// diagnostics.
    /// </remarks>
    public TimeSpan DueLookback { get; init; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Volunteers what is coming up: the owner's live facts that start in <c>(now, now + UpcomingWindow]</c>, in date
    /// order, in their own section. Default off, and off is byte-identical: the query is never issued.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Plan my prep for go-live week" does not resemble "I will be in Bariloche from December 9 to 13", so a
    /// similarity-scored recall never brings the trip, and the clash with the go-live is never seen. This is a
    /// <b>time-predicate</b> selection, like <see cref="ProspectiveFiring"/>: no embedding and no similarity floor.
    /// A fact starts at its <c>valid_from</c>, or at its <c>occurred_on</c> when it is an event.
    /// </para>
    /// <para>
    /// By default it fires only for a question that looks ahead (planning, scheduling, "this week", "upcoming",
    /// a future tense; see <see cref="UpcomingOnEveryRecall"/>), so a question about the past is not given a
    /// calendar it did not ask for. Cost when on: one owner-scoped read per such recall, its own budget
    /// (<see cref="MaxUpcomingItems"/>), never competing with <see cref="MaxFacts"/>.
    /// </para>
    /// </remarks>
    public bool Upcoming { get; init; }

    /// <summary>How far ahead <see cref="Upcoming"/> looks. 14 days by default.</summary>
    public TimeSpan UpcomingWindow { get; init; } = TimeSpan.FromDays(14);

    /// <summary>Budget for the upcoming section, its own claimant. 5 by default.</summary>
    public int MaxUpcomingItems { get; init; } = 5;

    /// <summary>
    /// Fires <see cref="Upcoming"/> on every recall rather than only for a question that looks ahead. Default off.
    /// </summary>
    public bool UpcomingOnEveryRecall { get; init; }

    /// <summary>
    /// Answers a question about a period with what is dated inside it. Default off, and off is byte-identical: the
    /// question is not parsed for a window and no query is issued.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The window comes from the question: "between December 7 and December 13", "on December 11", "this week",
    /// "next week", "the next 10 days", "tomorrow"; or from an event the question anchors it on: "go-live week" is
    /// the Monday-to-Sunday week of the owner's go-live, "launch day" the day of the launch, found by a dated fact
    /// that names it. The owner's live facts dated inside the window (an event on a day in it, or a stay that starts,
    /// ends or spans it) are recalled in their own section, in date order, and the window is reported on
    /// <c>MemoryContext.ResolvedTimeWindow</c>.
    /// </para>
    /// <para>
    /// A question that names a window is recalled against now, with the window's facts added; it does not take the
    /// as-of path of <c>MemoryOptions.ResolveTemporalQueries</c>, which answers a question about one past instant.
    /// A fact that is valid without an end (a job, a home) does not span every window: only bounded stays and
    /// dated events are window facts. Cost when on: a regex pass over the question, and for a resolved window one
    /// owner-scoped read (two for an anchored one).
    /// </para>
    /// </remarks>
    public bool TimeWindows { get; init; }

    /// <summary>Budget for the time-window section. 10 by default.</summary>
    public int MaxWindowItems { get; init; } = 10;

    /// <summary>
    /// Adds the owner's facts that overlap in time with the dated facts recalled for the question. Default off,
    /// and off is byte-identical: the query is never issued.
    /// </summary>
    /// <remarks>
    /// When recall brings "the go-live is on December 11", a trip from December 9 to 13 is the fact the person needs
    /// next to it, and nothing about it resembles the question. After the other sections resolve, their dated facts
    /// (events and bounded stays) give the windows; the owner's other live facts dated inside any of them are added
    /// in their own section. Overlap is in time only: whether two things clash is left to the reader, who sees both.
    /// Cost when on: one owner-scoped read per recall that recalled a dated fact.
    /// </remarks>
    public bool OverlapExpansion { get; init; }

    /// <summary>Budget for the overlapping section. 5 by default.</summary>
    public int MaxOverlapItems { get; init; } = 5;

    /// <summary>
    /// Add details of a recalled dated EVENT through its current decision-owned fact bindings.
    /// Default off: no extra service call, query, model call or prompt bytes. Requires an identity-capable
    /// backend. One live read from at most 10 dated facts and 20 event identities; pending proposals are not
    /// followed. Extra facts have no invented similarity score and share the existing total context budget.
    /// Point-in-time recall rejects this live-only option rather than reading future identity state.
    /// </summary>
    public bool EventDetails { get; init; }

    /// <summary>Maximum appended event details,0 disables the read; supported range 0..64. Default 8.</summary>
    public int MaxEventDetailFacts { get; init; } = 8;

    /// <summary>
    /// Reports aged-out memory as a stated absence when a fact section comes back thin. Default off,
    /// and off is byte-identical.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Forgetting already works and is <b>invisible</b>: decay prunes, recall returns less, and the
    /// agent answers as though it had never known â€” indistinguishable, to the person asking, from never
    /// having been told. This makes the gap sayable.
    /// </para>
    /// <para>
    /// The probe runs <b>only on thin recalls</b> and only when a query embedding exists, so a
    /// well-answered turn pays nothing. What surfaces is a summary â€” topic, count, dates â€” never the
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
    /// value configured on <c>MemoryOptions</c> â€” the same reference-equality inheritance
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
    /// hardcoded <c>null</c>, so nothing could ever reach it â€” a built, plumbed, unreachable option.
    /// It is now honoured on both recall paths: live and point-in-time (<c>AssembleContextAsOfAsync</c>,
    /// which forwarded a hardcoded <c>null</c> of its own for longer than the live path did).
    /// <para>
    /// It matters because a recalled trace is presented to the reader as precedent with nothing
    /// marking it as a failure, so imitating reasoning that did not work is worse than recalling
    /// nothing. Upstream <c>neo4j-labs/agent-memory</c> treats this as correctness rather than tuning
    /// and defaults its equivalent to successful-only. The default here stays at today's behaviour
    /// because nothing becomes a default before it is measured, and the trace surface has never been
    /// measured at all â€” it has carried a recall budget of zero in every quality run to date.
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

    /// <summary>
    /// E-1. Also returns facts filed under a <b>different name for the same thing</b>, when the
    /// conversation declared the two names equal. Default off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Similarity cannot cross an alias. "How many deliveries were taken at the new flat" does not
    /// retrieve "took a delivery at the place on Ferrow Row", because as strings those have nothing
    /// in common â€” and no embedding, threshold or re-ranker fixes that, since there is no similarity
    /// to find. Three measured interventions confirmed it: the write-side edge left the census
    /// exactly flat, and a hop re-ranker over the same edges made the vertical worse, because
    /// re-ranking can only reorder candidates retrieval already returned.
    /// </para>
    /// <para>
    /// <b>What makes this tractable is that the bridge is itself retrievable.</b> The declaration
    /// ("the new flat is the place on Ferrow Row") contains the asked-about name, so similarity finds
    /// THAT even when it cannot find the deliveries. One hop from it â€” out to the entity, back to the
    /// entity's other facts â€” reaches everything filed under either name.
    /// </para>
    /// <para>
    /// <b>Only entities carrying a declared alias are traversed</b>, which is the difference between
    /// this and the re-ranker that harmed. Hopping every <c>:ABOUT</c> edge pulls in facts related by
    /// nothing but sharing a subject, and measurably displaces better evidence; hopping only where an
    /// identity was actually asserted adds the facts the question needs and no others.
    /// </para>
    /// </remarks>
    public bool ExpandFactsByIdentity { get; init; }

    /// <summary>Cap on facts returned by identity expansion.</summary>
    /// <remarks>
    /// An alias sits on an entity, and an entity can carry many facts, so an uncapped hop would let
    /// one popular referent spend the whole budget. Separate from
    /// <see cref="MaxExpandedFacts"/> so the two expansions cannot mask each other's cost.
    /// </remarks>
    public int MaxIdentityExpandedFacts { get; init; } = 50;
}
