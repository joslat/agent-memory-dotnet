namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// Result of a memory extraction operation.
/// </summary>
public sealed record ExtractionResult
{
    /// <summary>
    /// Extracted entities.
    /// </summary>
    public IReadOnlyList<ExtractedEntity> Entities { get; init; } = Array.Empty<ExtractedEntity>();

    /// <summary>
    /// Extracted relationships.
    /// </summary>
    public IReadOnlyList<ExtractedRelationship> Relationships { get; init; } =
        Array.Empty<ExtractedRelationship>();

    /// <summary>
    /// Extracted facts.
    /// </summary>
    public IReadOnlyList<ExtractedFact> Facts { get; init; } = Array.Empty<ExtractedFact>();

    /// <summary>
    /// Extracted preferences.
    /// </summary>
    public IReadOnlyList<ExtractedPreference> Preferences { get; init; } =
        Array.Empty<ExtractedPreference>();

    /// <summary>
    /// Source message identifiers.
    /// </summary>
    public IReadOnlyList<string> SourceMessageIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Extraction metadata (e.g., model used, extraction time).
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } =
        new Dictionary<string, object>();

    /// <summary>
    /// Overall ingestion outcome (#101). Defaults to <see cref="IngestionStatus.Succeeded"/> so existing
    /// callers that only read the item collections above see unchanged behavior.
    /// </summary>
    public IngestionStatus Status { get; init; } = IngestionStatus.Succeeded;

    /// <summary>
    /// Per-item outcomes recorded across the extraction and persistence stages (#101): failures,
    /// meaningful skips, and successes. Empty by default. See <see cref="IngestionItemOutcome"/>.
    /// </summary>
    public IReadOnlyList<IngestionItemOutcome> Outcomes { get; init; } = Array.Empty<IngestionItemOutcome>();

    /// <summary>Convenience accessor: <see langword="true"/> when <see cref="Status"/> is <see cref="IngestionStatus.PartiallySucceeded"/>.</summary>
    public bool IsPartial => Status == IngestionStatus.PartiallySucceeded;
}
