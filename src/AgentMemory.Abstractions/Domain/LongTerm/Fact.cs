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
