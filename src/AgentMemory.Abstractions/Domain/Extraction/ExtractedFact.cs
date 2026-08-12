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
