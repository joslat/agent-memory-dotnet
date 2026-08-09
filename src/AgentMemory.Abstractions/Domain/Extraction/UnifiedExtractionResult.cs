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
}
