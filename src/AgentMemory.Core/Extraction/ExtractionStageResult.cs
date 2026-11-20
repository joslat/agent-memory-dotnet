using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Extraction;

/// <summary>
/// Internal DTO carrying the output of <see cref="ExtractionStage"/> into
/// <see cref="PersistenceStage"/>. Holds both the raw items (for returning to callers)
/// and the resolved/filtered items (for embedding and persistence).
/// </summary>
internal sealed record ExtractionStageResult
{
    public ExtractionPath Path { get; init; }
    /// <summary>Side-effect-free original observations and proposed identity decisions, present only in the opt-in mode.</summary>
    public IReadOnlyList<IdentityDecisionWrite> IdentityPlans { get; init; } = Array.Empty<IdentityDecisionWrite>();

    /// <summary>Frozen original relationship claims with optional decision-owned endpoint support.</summary>
    public IReadOnlyList<IdentityRelationshipObservation> IdentityRelationships { get; init; } = Array.Empty<IdentityRelationshipObservation>();

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

    /// <summary>Preserves explicit source visibility even when no entity mention was extracted.</summary>
    public bool IdentityIncludeSharedSources { get; init; }

    public IReadOnlyList<string> SourceMessageIds { get; init; } = Array.Empty<string>();

    /// <summary>What the extracted turns said, for a judge on the write path (41.06); empty when nothing was extracted.</summary>
    public string SourceText { get; init; } = "";

    /// <summary>
    /// When the extracted turns were said (the latest message time); the judge on the write path reads relative dates
    /// against it, so a turn written later means what it meant when it was said. Null: unknown, the clock is used.
    /// </summary>
    public DateTimeOffset? SourceSaidAtUtc { get; init; }

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

    /// <summary>Stored facts the writer said the turn says again: persistence reinforces them.</summary>
    public IReadOnlyList<string> ConfirmedFactIds { get; init; } = Array.Empty<string>();

    /// <summary>Stored preferences the writer said the turn says again: persistence reinforces them.</summary>
    public IReadOnlyList<string> ConfirmedPreferenceIds { get; init; } = Array.Empty<string>();

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

    /// <summary>An applied-once write (<c>ExtractionRequest.OperationId</c>): the operation and the message its receipt goes on. Null otherwise.</summary>
    public AppliedOnceOperation? AppliedOnce { get; init; }

    /// <summary>
    /// An applied-once write: the judge's verdicts on the writer's named replacements, taken before the store transaction
    /// (<see cref="JudgedMemoryClosings.PrejudgeNamedAsync"/>). Null: persistence asks the judge itself, as before.
    /// </summary>
    public IReadOnlyDictionary<string, double>? PrejudgedNamedVerdicts { get; init; }
}

/// <summary>The operation an applied-once write applies, and the stored message its receipt is written on.</summary>
internal sealed record AppliedOnceOperation(string OperationId, string MessageId);
