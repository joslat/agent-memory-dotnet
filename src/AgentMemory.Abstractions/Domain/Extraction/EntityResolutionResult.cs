namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// Result of entity resolution with metadata about how the entity was resolved.
/// </summary>
public sealed record EntityResolutionResult
{
    /// <summary>The resolved (canonical) entity to use going forward.</summary>
    public required Entity ResolvedEntity { get; init; }

    /// <summary>How the entity was resolved (exact / fuzzy / semantic / new).</summary>
    public required EntityMatchType MatchType { get; init; }

    /// <summary>Confidence score from 0.0 to 1.0.</summary>
    public required double Confidence { get; init; }

    /// <summary>Id of the source entity that was merged into the resolved entity, if any.</summary>
    public string? MergedFromEntityId { get; init; }

    /// <summary>Message ids that contributed to this resolution.</summary>
    public IReadOnlyList<string> SourceMessageIds { get; init; } = Array.Empty<string>();
}
