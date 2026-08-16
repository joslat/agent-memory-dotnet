using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Options;

/// <summary>Configuration for the extraction pipeline.</summary>
public sealed class ExtractionOptions
{
    /// <summary>Entity resolution strategy options.</summary>
    public EntityResolutionOptions EntityResolution { get; set; } = new();
    /// <summary>Entity validation filter options.</summary>
    public EntityValidationOptions Validation { get; set; } = new();
    /// <summary>Merge strategy when multiple extractors are registered.</summary>
    public MergeStrategyType MergeStrategy { get; set; } = MergeStrategyType.Union;
    /// <summary>Minimum confidence to accept an extracted entity.</summary>
    public double MinConfidenceThreshold { get; set; } = 0.5;
    /// <summary>When true, entities above <see cref="AutoMergeThreshold"/> are automatically merged.</summary>
    public bool EnableAutoMerge { get; set; } = true;
    /// <summary>Confidence threshold that triggers auto-merge (≥ this value).</summary>
    public double AutoMergeThreshold { get; set; } = 0.95;
    /// <summary>Confidence threshold that adds a SAME_AS relationship (between this and <see cref="AutoMergeThreshold"/>).</summary>
    public double SameAsThreshold { get; set; } = 0.85;
    /// <summary>Confidence assigned to strong regex patterns in PatternBasedPreferenceDetector.</summary>
    public double StrongPatternConfidence { get; set; } = 0.95;
    /// <summary>Confidence assigned to standard regex matches in PatternBasedPreferenceDetector.</summary>
    public double RegexMatchConfidence { get; set; } = 0.85;
    /// <summary>
    /// How the pipeline reacts to a per-item ingestion failure (#101). Defaults to
    /// <see cref="IngestionFailureMode.BestEffort"/> -- today's behavior, unchanged.
    /// </summary>
    public IngestionFailureMode FailureMode { get; set; } = IngestionFailureMode.BestEffort;

    /// <summary>
    /// Uses atomic repository batch upserts when the configured repository explicitly advertises
    /// support. Best-effort mode falls back to the existing item path if a batch fails, preserving
    /// per-item outcomes. Fail-fast mode retains item upserts inside its outer atomic transaction so
    /// the exception can still identify the exact failing item. Defaults to <see langword="true"/>.
    /// </summary>
    public bool EnableBatchMemoryUpserts { get; set; } = true;

    /// <summary>
    /// Generates missing entity, fact, and preference embeddings in one aligned provider batch
    /// before persistence. Result-count mismatches and unavailable vectors replay safely through
    /// the existing single-item path. Defaults to <see langword="true"/>; disable to preserve the
    /// legacy one-request-per-learned-item behavior.
    /// </summary>
    public bool UseBatchEmbeddingRequests { get; set; } = true;

    /// <summary>
    /// Reuses owner/type entity-resolution candidates within one multi-session extraction batch.
    /// Candidate types are prefetched concurrently, while identity decisions remain chronological
    /// and update the request-local snapshot after each resolution. Defaults to <see langword="true"/>.
    /// Disable to retain one repository candidate lookup per extracted entity.
    /// </summary>
    public bool UseBatchEntityResolutionSnapshots { get; set; } = true;

    /// <summary>
    /// Coalesces the successful repository work for one logical best-effort persistence operation
    /// into one atomic store transaction when the provider can prove rollback. External embedding
    /// work completes before the transaction opens. If any item fails, the coalesced attempt rolls
    /// back and replays through the legacy item-isolated path; providers without atomic rollback
    /// retain that path directly. Defaults to <see langword="true"/>; disable to preserve one
    /// transaction per repository operation.
    /// </summary>
    public bool UseCoalescedPersistenceTransactions { get; set; } = true;

    /// <summary>
    /// The trust level stamped on every entity/fact/preference persisted, unless a specific
    /// <c>ExtractionRequest.TrustLevel</c> overrides it for that call (#92 Phase 3). Defaults to
    /// <see cref="MemoryTrustLevel.UserProvided"/> -- everything extracted through the normal pipeline is
    /// derived from real conversation content, distinct from content a host explicitly marks
    /// <see cref="MemoryTrustLevel.ApplicationTrusted"/>.
    /// </summary>
    public MemoryTrustLevel DefaultTrustLevel { get; set; } = MemoryTrustLevel.UserProvided;

    /// <summary>
    /// Whether a newly written fact about a <b>functional</b> relation supersedes the earlier
    /// assertion it replaces, instead of accumulating beside it (M1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Off by default. It changes what live recall returns — a superseded fact drops out of it — and
    /// every recorded measurement was taken with append-only writes, so a default flip would move
    /// results with no setting changed.
    /// </para>
    /// <para>
    /// <b>Only relations the vocabulary declares functional are eligible</b> (<c>lives in</c>,
    /// <c>works at</c>, …). A person likes many things and attends many events; superseding a
    /// multi-valued predicate would close a true fact. Undeclared predicates, including any the
    /// extractor invents, are treated as multi-valued and are never superseded.
    /// </para>
    /// <para>
    /// Non-destructive: losers keep their content, gain <c>invalidated_at</c> and a
    /// <c>:SUPERSEDED_BY</c> edge, and stay visible to as-of recall. Requires a store implementing
    /// <c>IFactRepository.FindSupersededCandidatesAsync</c>; one that does not simply keeps appending.
    /// </para>
    /// </remarks>
    public bool SupersedeReplacedFacts { get; set; }

    /// <summary>
    /// Skips the extraction call entirely for turns that cannot carry a fact — greetings, thanks,
    /// bare acknowledgement (E4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The extraction call is where the money is: the write is one round trip to Neo4j, while
    /// extraction is a model call over the rendered transcript and on a corpus build it is
    /// essentially the whole bill. An "ok, thanks!" turn pays it for nothing.
    /// </para>
    /// <para>
    /// <b>Deliberately not a write-side gate.</b> Skipping the persistence of a triple already in the
    /// store looks like the more obvious saving and would silently disable two features that depend
    /// on the duplicate write happening: <see cref="MemoryOptions.ConfidenceReinforcementAlpha"/>,
    /// where a re-asserted fact earns α, and the <c>mention_count</c> the salience reranker reads.
    /// Corroboration <i>is</i> the repeated write.
    /// </para>
    /// <para>
    /// Detection is a closed vocabulary, deterministic, and biased hard toward extracting: a missed
    /// skip costs one call, while gating a turn that did carry a fact means the memory is never
    /// formed and nothing downstream can recover it. "yes", "no" and "sure" are therefore treated as
    /// contentful — each is a complete answer to a question.
    /// </para>
    /// <para>
    /// Off by default: it changes what gets extracted, and prompt-and-transcript bytes are
    /// fingerprinted into every measured run.
    /// </para>
    /// </remarks>
    public bool SkipUninformativeTurns { get; set; }

    /// <summary>
    /// How many earlier turns to hand the extractors as read-only context (E2). <c>0</c> — the
    /// default — is the pre-E2 behaviour of extracting from the batch alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One batch is often not enough to understand itself: "I moved there last year" needs the turn
    /// that named the place, and "she recommended it" needs the turn that named her. Without context
    /// those either extract nothing or extract an unresolved pronoun as an entity.
    /// </para>
    /// <para>
    /// <b>Context turns are read, never extracted from, and never attributed to.</b> Extracting them
    /// would re-assert facts already stored — which now earns confidence
    /// (<see cref="MemoryOptions.ConfidenceReinforcementAlpha"/>) and increments the
    /// <c>mention_count</c> the salience reranker reads, so a fact would gain both merely by sitting
    /// inside a sliding window. Corroboration would become recency.
    /// </para>
    /// <para>
    /// Costs input tokens on every extraction, which is why it is off rather than defaulted to a
    /// nominal value. Zep uses 4.
    /// </para>
    /// </remarks>
    public int ExtractionContextTurns { get; set; }

    /// <summary>
    /// The session accountant: materialises aggregates from what a batch just persisted. Off by default.
    /// </summary>
    /// <remarks>
    /// Sits on extraction options because the accountant runs as a post-persistence pass over exactly
    /// the groups the batch touched — it is part of writing, not of reading. Recall needs no changes at
    /// all: a derived fact <i>is</i> a <c>:Fact</c>, so it rides the existing vector index, budget,
    /// owner scoping, invalidation gate and valid-time gate for free.
    /// </remarks>
    public DerivedMemoryOptions DerivedMemory { get; set; } = new();
}

/// <summary>Controls which matching strategies are used for entity resolution.</summary>
public sealed class EntityResolutionOptions
{
    /// <summary>Enable exact name matching.</summary>
    public bool EnableExactMatch { get; set; } = true;
    /// <summary>Enable fuzzy (edit-distance) name matching.</summary>
    public bool EnableFuzzyMatch { get; set; } = true;
    /// <summary>Enable semantic (embedding) matching.</summary>
    public bool EnableSemanticMatch { get; set; } = true;
    /// <summary>
    /// When true (default), only same-type entities are candidates for a match. When false, entities
    /// sharing the incoming name (or carrying it as an alias) are also candidates, whatever their type.
    /// </summary>
    /// <remarks>
    /// Turn it off when the extractor's typing is unreliable — the same real-world entity arriving as
    /// <c>Organization</c> in one turn and <c>Location</c> in the next is, under strict filtering,
    /// permanently two entities. The cost is one extra bounded read per resolution; the owner boundary
    /// is unaffected either way.
    /// </remarks>
    public bool TypeStrictFiltering { get; set; } = true;
    /// <summary>Minimum similarity score for a fuzzy match to be considered.</summary>
    public double FuzzyMatchThreshold { get; set; } = 0.85;
    /// <summary>Minimum cosine similarity for a semantic match to be considered.</summary>
    public double SemanticMatchThreshold { get; set; } = 0.8;
}

/// <summary>Controls validation rules applied to extracted entity candidates.</summary>
public sealed class EntityValidationOptions
{
    /// <summary>Minimum character length for an entity name to be accepted.</summary>
    public int MinNameLength { get; set; } = 2;
    /// <summary>Reject entities whose names consist entirely of digits.</summary>
    public bool RejectNumericOnly { get; set; } = true;
    /// <summary>Reject entities whose names consist entirely of punctuation.</summary>
    public bool RejectPunctuationOnly { get; set; } = true;
    /// <summary>Reject entities whose names are common stop-words.</summary>
    public bool UseStopwordFilter { get; set; } = true;

}
