namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Represents a factual statement extracted from conversations.
/// </summary>
public sealed record Fact
{
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
    /// Additional metadata.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } =
        new Dictionary<string, object>();
}
