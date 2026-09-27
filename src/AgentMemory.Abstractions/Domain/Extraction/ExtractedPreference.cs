namespace AgentMemory.Abstractions.Domain;

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

    /// <inheritdoc cref="ExtractedFact.SourceRole"/>
    /// <remarks>
    /// Preferences carry this for the same reason facts do, and arguably a stronger one: a preference
    /// the <i>assistant</i> attributed to the user ("you seem to prefer …") becomes a durable statement
    /// about that user, and is indistinguishable after the fact from one the user actually stated.
    /// </remarks>
    public string? SourceRole { get; init; }

    /// <inheritdoc cref="ExtractedFact.SourceTurn"/>
    public int? SourceTurn { get; init; }

    /// <summary>
    /// 36.4. The earlier value this statement corrects or replaces, as it was said ("Arcade Fire, not Radiohead"
    /// replaces <c>Radiohead</c>), or null. Written by the extractor only when asked
    /// (<c>LlmExtractionOptions.MarkCorrections</c>); read once, at the write, to close what it replaces.
    /// </summary>
    public string? Replaces { get; init; }
}
