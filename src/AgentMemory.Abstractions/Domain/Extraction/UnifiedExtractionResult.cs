namespace AgentMemory.Abstractions.Domain;

/// <summary>Typed result of one model call that extracts every supported memory category.</summary>
public sealed record UnifiedExtractionResult
{
    /// <summary>Extracted entities.</summary>
    public IReadOnlyList<ExtractedEntity> Entities { get; init; } = Array.Empty<ExtractedEntity>();
    /// <summary>Extracted facts.</summary>
    public IReadOnlyList<ExtractedFact> Facts { get; init; } = Array.Empty<ExtractedFact>();
    /// <summary>Extracted preferences.</summary>
    public IReadOnlyList<ExtractedPreference> Preferences { get; init; } = Array.Empty<ExtractedPreference>();
    /// <summary>Extracted entity relationships.</summary>
    public IReadOnlyList<ExtractedRelationship> Relationships { get; init; } = Array.Empty<ExtractedRelationship>();

    /// <summary>
    /// AMWRITE001. Stored facts, by id, that a store-aware writer said the turn says again (its "confirm"): reinforced as a
    /// repeated write is (one more mention, confidence by <c>MemoryOptions.ConfidenceReinforcementAlpha</c>), never stored twice.
    /// </summary>
    public IReadOnlyList<string> ConfirmedFactIds { get; init; } = Array.Empty<string>();

    /// <summary>AMWRITE001. Stored preferences, by id, the turn says again: reinforced, never stored twice.</summary>
    public IReadOnlyList<string> ConfirmedPreferenceIds { get; init; } = Array.Empty<string>();
}
