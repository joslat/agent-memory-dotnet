namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// Represents a factual statement extracted from conversations.
/// </summary>
public sealed record Fact
{
    /// <summary>Metadata key recording how a recalled fact entered the context.</summary>
    /// <remarks>
    /// Facts reaching the context by canonical-predicate expansion may legitimately carry provenance
    /// outside the current query's window, because expansion returns a relation across the whole
    /// owner rather than only what the query itself matched. Consumers that resolve provenance must
    /// be able to tell that apart from a fact whose source genuinely cannot be resolved, which is
    /// corruption. Marking the former keeps the latter detectable.
    /// </remarks>
    public const string RetrievalSourceMetadataKey = "agentMemory.retrievalSource";

    /// <summary>Value of <see cref="RetrievalSourceMetadataKey"/> for predicate-expanded facts.</summary>
    public const string RetrievalSourcePredicateExpansion = "predicate-expansion";

    /// <summary>
    /// Unique identifier for the fact.
    /// </summary>
    public required string FactId { get; init; }

    /// <summary>
    /// Subject of the fact (typically an entity or concept).
    /// </summary>
    public required string Subject { get; init; }

    /// <summary>
    /// Predicate or relationship type.
    /// </summary>
    public required string Predicate { get; init; }

    /// <summary>
    /// Object or value of the fact.
    /// </summary>
    public required string Object { get; init; }

    /// <summary>
    /// Confidence score (0.0 to 1.0) for the extraction.
    /// </summary>
    public required double Confidence { get; init; }

    /// <summary>
    /// Optional start of validity period.
    /// </summary>
    public DateTimeOffset? ValidFrom { get; init; }

    /// <summary>
    /// Optional end of validity period.
    /// </summary>
    public DateTimeOffset? ValidUntil { get; init; }

    /// <summary>
    /// When this fact was superseded or otherwise invalidated; <see langword="null"/> while it is live.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <b>transaction</b> clock, distinct from <see cref="ValidUntil"/>'s real-world one:
    /// <c>ValidUntil</c> says when a fact stopped being true, this says when the store stopped
    /// believing it. A contradicted fact is invalidated without its validity period changing at all.
    /// </para>
    /// <para>
    /// The store has always recorded this — supersession is non-destructive precisely so as-of recall
    /// can still reach it — but it was never projected onto the domain record, so a caller reading
    /// facts had no way to tell a superseded one from a live one. Anything deriving from a fact set
    /// (S1's summaries first among them) needs exactly that distinction.
    /// </para>
    /// </remarks>
    public DateTimeOffset? InvalidatedAtUtc { get; init; }

    /// <summary>
    /// Why this fact stopped being live — <c>'decay'</c> when the prune let it go, null otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="InvalidatedAtUtc"/> alone cannot tell a fact that <b>decayed</b> from one that was
    /// <b>contradicted</b>, and the two need opposite treatment on read. A superseded fact was replaced
    /// by something better and its replacement is what should surface. A decayed one is knowledge the
    /// system quietly let go of — the only kind that can honestly be reported back as "I used to know
    /// something about this and no longer do".
    /// </para>
    /// <para>
    /// Null is the partition, and it is also the honest value for everything invalidated before this
    /// property existed: those facts have an unknowable reason and are simply never reported as
    /// forgotten. A disclosed start-at-deployment limit rather than a backfilled guess.
    /// </para>
    /// </remarks>
    public string? InvalidatedReason { get; init; }

    /// <summary>
    /// Optional embedding vector for semantic search.
    /// </summary>
    public float[]? Embedding { get; init; }

    /// <summary>
    /// Source message references for provenance.
    /// </summary>
    public IReadOnlyList<string> SourceMessageIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// UTC timestamp when the fact was created.
    /// </summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>
    /// Optional category for grouping facts (e.g., "personal", "professional", "preferences").
    /// Used for index-based retrieval and schema bootstrapping.
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    /// Optional owner/user id that scopes this record. Null means shared/global (visible to
    /// everyone). See <c>MemoryScope</c> and docs/archive/Memory_Review_and_Implementation_Plan.md (R1).
    /// </summary>
    public string? OwnerId { get; init; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } =
        new Dictionary<string, object>();
}
