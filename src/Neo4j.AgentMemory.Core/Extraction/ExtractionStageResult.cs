using Neo4j.AgentMemory.Abstractions.Domain;

namespace Neo4j.AgentMemory.Core.Extraction;

/// <summary>
/// Internal DTO carrying the output of <see cref="ExtractionStage"/> into
/// <see cref="PersistenceStage"/>. Holds both the raw items (for returning to callers)
/// and the resolved/filtered items (for embedding and persistence).
/// </summary>
internal sealed record ExtractionStageResult
{
    // ── Raw extracted items — returned to callers via ExtractionResult ──

    public IReadOnlyList<ExtractedEntity> RawEntities { get; init; } = Array.Empty<ExtractedEntity>();
    public IReadOnlyList<ExtractedFact> RawFacts { get; init; } = Array.Empty<ExtractedFact>();
    public IReadOnlyList<ExtractedPreference> RawPreferences { get; init; } = Array.Empty<ExtractedPreference>();
    public IReadOnlyList<ExtractedRelationship> RawRelationships { get; init; } = Array.Empty<ExtractedRelationship>();

    // ── Processed items — ready for embedding and persistence ──

    /// <summary>
    /// Entities that passed confidence filter + validation + resolution.
    /// Key = original extracted name (for relationship ID look-up).
    /// Value = resolved <see cref="Entity"/> (no embedding yet).
    /// </summary>
    public IReadOnlyDictionary<string, Entity> ResolvedEntityMap { get; init; } =
        new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Facts that passed the confidence threshold.</summary>
    public IReadOnlyList<ExtractedFact> FilteredFacts { get; init; } = Array.Empty<ExtractedFact>();

    /// <summary>Preferences that passed the confidence threshold.</summary>
    public IReadOnlyList<ExtractedPreference> FilteredPreferences { get; init; } = Array.Empty<ExtractedPreference>();

    /// <summary>Relationships that passed the confidence threshold AND have both endpoints resolved.</summary>
    public IReadOnlyList<ExtractedRelationship> FilteredRelationships { get; init; } = Array.Empty<ExtractedRelationship>();

    // ── Provenance ──

    public IReadOnlyList<string> SourceMessageIds { get; init; } = Array.Empty<string>();

    // ── Metadata ──

    public MergeStrategyType MergeStrategy { get; init; }
    public int EntityExtractorCount { get; init; }
    public int FactExtractorCount { get; init; }
    public int PreferenceExtractorCount { get; init; }
    public int RelationshipExtractorCount { get; init; }
}
