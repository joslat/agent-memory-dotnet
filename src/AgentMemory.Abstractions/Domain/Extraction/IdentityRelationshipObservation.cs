namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// Original relationship assertion, independent of endpoint identity. Missing endpoint support
/// preserves an unresolved claim; it never grants equality or creates an ordinary shared edge.
/// </summary>
public sealed record IdentityRelationshipObservation
{
    /// <summary>Stable owner/source/original-assertion identifier, reused on retries.</summary>
    public required string ObservationId { get; init; }
    /// <summary>Original names, relation type, description, attributes and writer intent.</summary>
    public required ExtractedRelationship Observation { get; init; }
    /// <summary>Exact write owner. Null denotes shared data.</summary>
    public string? OwnerId { get; init; }
    /// <summary>Bounded root messages supporting the original assertion.</summary>
    public IReadOnlyList<string> SourceMessageIds { get; init; } = Array.Empty<string>();
    /// <summary>Decision for the original source mention, absent when it is unresolved or ambiguous.</summary>
    public string? SourceDecisionId { get; init; }
    /// <summary>Decision for the original target mention, absent when it is unresolved or ambiguous.</summary>
    public string? TargetDecisionId { get; init; }
    /// <summary>Original observation time; a replay does not replace it.</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }
    /// <summary>First explicit relationship end, independent of later endpoint interpretation.</summary>
    public DateTimeOffset? EndedAtUtc { get; init; }
}
