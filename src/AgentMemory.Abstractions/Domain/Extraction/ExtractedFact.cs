namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// A fact extracted from text, before persistence.
/// </summary>
public sealed record ExtractedFact
{
    /// <summary>
    /// Subject of the fact.
    /// </summary>
    public required string Subject { get; init; }

    /// <summary>
    /// Predicate or relationship type.
    /// </summary>
    public required string Predicate { get; init; }

    /// <summary>
    /// Object or value.
    /// </summary>
    public required string Object { get; init; }

    /// <summary>
    /// Confidence score (0.0 to 1.0).
    /// </summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>
    /// Optional start of validity period.
    /// </summary>
    public DateTimeOffset? ValidFrom { get; init; }

    /// <summary>
    /// Optional end of validity period.
    /// </summary>
    public DateTimeOffset? ValidUntil { get; init; }

    /// <summary>How precisely <see cref="ValidFrom"/> was stated (36.1); <see cref="DatePrecision.Unspecified"/> renders as a day.</summary>
    public DatePrecision ValidFromPrecision { get; init; }

    /// <summary>How precisely <see cref="ValidUntil"/> was stated (36.1); <see cref="DatePrecision.Unspecified"/> renders as a day.</summary>
    public DatePrecision ValidUntilPrecision { get; init; }

    /// <summary>
    /// 36.1. The day a one-off event happened ("yesterday I went hiking in Sintra"), or null for a state. An event is not
    /// a validity period: it stays true once it happened, so it carries neither <see cref="ValidFrom"/> (which would read
    /// "since") nor <see cref="ValidUntil"/> (which would expire it from the profile the day after).
    /// </summary>
    public DateTimeOffset? OccurredOn { get; init; }

    /// <summary>How precisely <see cref="OccurredOn"/> was stated (36.1).</summary>
    public DatePrecision OccurredOnPrecision { get; init; }

    /// <summary>
    /// 36.4. The earlier value this statement corrects or replaces, as it was said ("Arcade Fire, not Radiohead"
    /// replaces <c>Radiohead</c>), or null. Written by the extractor only when asked
    /// (<c>LlmExtractionOptions.MarkCorrections</c>); read once, at the write, to close what it replaces.
    /// </summary>
    public string? Replaces { get; init; }

    /// <summary>
    /// The conversational role of the turn this fact was derived from (<c>"user"</c>,
    /// <c>"assistant"</c>, …), or <see langword="null"/> when the extractor did not report one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Trust is otherwise stamped <b>once per extraction request</b> and applied to every item in the
    /// batch, so a batch containing both a user's statement and a claim the model itself made records
    /// them identically. That is tolerable only while assistant content is not extracted at all — which
    /// is the shipped default (<c>AssistantContentMode.Ignore</c>) — and stops being tolerable the
    /// moment it is switched on, because the enum's central distinction between a user's claim and the
    /// model's own would be lost at exactly the point it first carries weight.
    /// </para>
    /// <para>
    /// <b>Null is the meaningful value, and it means "unchanged".</b> Extractors populate this only when
    /// assistant content is being extracted, so at defaults it is null everywhere and persistence
    /// applies the request's trust level exactly as it always did. It is a self-report by the model
    /// rather than a derived fact — a per-item source binding would need per-item provenance, which the
    /// batch-level <c>EXTRACTED_FROM</c> edge does not yet carry — so it may only <i>refine</i> a trust
    /// stamp, never relax the guarantees around it.
    /// </para>
    /// </remarks>
    public string? SourceRole { get; init; }

    /// <summary>
    /// The 1-based turn number this fact was stated in, or <see langword="null"/> when the extractor
    /// did not report one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Populated only under <see cref="Options.ExtractionProvenanceMode.PerItem"/>, which numbers the
    /// turns in the extraction transcript and asks which one stated each item. It resolves to a single
    /// source message, replacing the batch-level link in which a fact points at a mean of 12 messages
    /// and as many as 30 — a breadth that makes any attribution metric derived from the edge true by
    /// construction.
    /// </para>
    /// <para>
    /// Out of range or absent falls back to the batch links. Coarse provenance is recoverable; missing
    /// provenance is not, and a hallucinated turn number must not be able to erase the real answer.
    /// </para>
    /// </remarks>
    public int? SourceTurn { get; init; }
}
