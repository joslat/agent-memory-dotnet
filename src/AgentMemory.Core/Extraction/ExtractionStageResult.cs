using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Extraction;

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

    /// <summary>What the extracted turns said, for a judge on the write path (41.06); empty when nothing was extracted.</summary>
    public string SourceText { get; init; } = "";

    // ── Metadata ──

    public MergeStrategyType MergeStrategy { get; init; }
    public int EntityExtractorCount { get; init; }
    public int FactExtractorCount { get; init; }
    public int PreferenceExtractorCount { get; init; }
    public int RelationshipExtractorCount { get; init; }

    /// <summary>
    /// True when a store-aware writer (<see cref="AgentMemory.Abstractions.Services.IMemoryWriter"/>) decided these items
    /// instead of the extractors: persistence then closes only the stored memories the writer named
    /// (<see cref="ExtractedFact.ReplacesId"/>), each confirmed by the update judge, and asks the judge about nothing else.
    /// </summary>
    public bool WrittenByWriter { get; init; }

    /// <summary>
    /// Why the extractors wrote this turn instead of an enabled store-aware writer, when its call failed and
    /// <see cref="AgentMemory.Abstractions.Options.ExtractionOptions.FallBackToExtractorsWhenWriterFails"/> is on (the writer's error); null otherwise.
    /// </summary>
    public string? WriterFallbackReason { get; init; }

    /// <summary>
    /// Item outcomes recorded during this stage (extractor failures, validation/resolution
    /// failures and skips) -- see <see cref="IngestionItemOutcome"/> (#101). Carried forward and
    /// appended to by <see cref="IPersistenceStage"/>.
    /// </summary>
    public IReadOnlyList<IngestionItemOutcome> Outcomes { get; init; } = Array.Empty<IngestionItemOutcome>();
}
