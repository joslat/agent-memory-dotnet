namespace AgentMemory.Abstractions.Domain;

/// <summary>Contextual model interpretation; quotes establish provenance, not semantic truth.</summary>
public enum IdentityAdjudicationVerdict
{
    /// <summary>The model proposes the offered identity.</summary>
    Same,
    /// <summary>The model distinguishes the offered identity.</summary>
    Different,
    /// <summary>The offered evidence does not resolve identity.</summary>
    Uncertain
}

/// <summary>An exact quote from an offered, source-addressable message.</summary>
public sealed record IdentityAdjudicationCitation
{
    /// <summary>The offered source identifier; never a model-invented reference.</summary>
    public required string SourceId { get; init; }
    /// <summary>Exact source substring, bounded to 2,048 characters.</summary>
    public required string Quote { get; init; }
}

/// <summary>Typed, bounded contextual receipt retained independently of opaque evidence JSON.</summary>
public sealed record IdentityAdjudicationEvidence
{
    /// <summary>The parsed model verdict; type policy still controls confirmation authority.</summary>
    public required IdentityAdjudicationVerdict Verdict { get; init; }
    /// <summary>An offered candidate, required for same/different interpretations.</summary>
    public string? CandidateId { get; init; }
    /// <summary>At most 32 exact supporting quotes; same/different require at least one.</summary>
    public IReadOnlyList<IdentityAdjudicationCitation> Citations { get; init; } = Array.Empty<IdentityAdjudicationCitation>();
}
