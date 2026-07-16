using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Extraction;

/// <summary>
/// Embeds and persists items produced by <see cref="IExtractionStage"/>.
/// </summary>
internal interface IPersistenceStage
{
    /// <summary>
    /// Embeds entities, facts, and preferences, upserts them to their repositories,
    /// and wires EXTRACTED_FROM provenance relationships. The optional <c>ownerId</c> is stamped on
    /// every persisted entity/fact/preference (null = shared/global; see MemoryScope, R1). Honors the
    /// configured <c>ExtractionOptions.FailureMode</c> (#101): under <c>FailFast</c>, throws
    /// <c>MemoryIngestionException</c> at the first item that fails at this stage.
    /// </summary>
    Task<PersistenceResult> PersistAsync(
        ExtractionStageResult extraction,
        string? ownerId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Summary counts returned by <see cref="IPersistenceStage.PersistAsync"/>.
/// </summary>
internal sealed record PersistenceResult
{
    public int EntityCount { get; init; }
    public int FactCount { get; init; }
    public int PreferenceCount { get; init; }
    public int RelationshipCount { get; init; }

    /// <summary>
    /// The complete outcome list: every outcome carried in from the extraction stage, plus every
    /// outcome (success, skip, or failure) recorded during this stage (#101).
    /// </summary>
    public IReadOnlyList<IngestionItemOutcome> Outcomes { get; init; } = Array.Empty<IngestionItemOutcome>();
}
