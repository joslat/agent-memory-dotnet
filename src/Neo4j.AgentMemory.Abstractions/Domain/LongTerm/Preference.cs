namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Represents a user preference extracted from conversations.
/// </summary>
public sealed record Preference
{
    /// <summary>
    /// Unique identifier for the preference.
    /// </summary>
    public required string PreferenceId { get; init; }

    /// <summary>
    /// Category of the preference (e.g., "communication", "style", "feature").
    /// </summary>
    public required string Category { get; init; }

    /// <summary>
    /// Text description of the preference.
    /// </summary>
    public required string PreferenceText { get; init; }

    /// <summary>
    /// Optional context in which the preference applies.
    /// </summary>
    public string? Context { get; init; }

    /// <summary>
    /// Confidence score (0.0 to 1.0) for the extraction.
    /// </summary>
    public required double Confidence { get; init; }

    /// <summary>
    /// Optional embedding vector for semantic search.
    /// </summary>
    public float[]? Embedding { get; init; }

    /// <summary>
    /// Source message references for provenance.
    /// </summary>
    public IReadOnlyList<string> SourceMessageIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// UTC timestamp when the preference was created.
    /// </summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } =
        new Dictionary<string, object>();
}
