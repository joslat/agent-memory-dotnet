namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// A preference extracted from text, before persistence.
/// </summary>
public sealed record ExtractedPreference
{
    /// <summary>
    /// Category of the preference.
    /// </summary>
    public required string Category { get; init; }

    /// <summary>
    /// Text description of the preference.
    /// </summary>
    public required string PreferenceText { get; init; }

    /// <summary>
    /// Optional context.
    /// </summary>
    public string? Context { get; init; }

    /// <summary>
    /// Confidence score (0.0 to 1.0).
    /// </summary>
    public double Confidence { get; init; } = 1.0;
}
