namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// Machine-readable outcome of one memory item at one ingestion stage (#101). A single logical item
/// (e.g. one fact) may have more than one outcome across the pipeline -- e.g. a
/// <see cref="IngestionStage.Persistence"/> success followed by a <see cref="IngestionStage.Provenance"/>
/// failure -- since each stage's result is independently meaningful for retries and auditing.
/// </summary>
public sealed record IngestionItemOutcome
{
    /// <summary>The kind of item this outcome describes.</summary>
    public required MemoryItemKind Kind { get; init; }

    /// <summary>The pipeline stage this outcome was recorded at.</summary>
    public required IngestionStage Stage { get; init; }

    /// <summary>Whether the item succeeded, was skipped, or failed at this stage.</summary>
    public required IngestionItemStatus Status { get; init; }

    /// <summary>
    /// A human-readable identifier for the item, for correlating this outcome back to the source data
    /// (e.g. an entity name, or "Subject Predicate Object" for a fact). Not a persisted identifier.
    /// </summary>
    public string? SourceKey { get; init; }

    /// <summary>The persisted node/edge id, when the item reached persistence successfully.</summary>
    public string? PersistedId { get; init; }

    /// <summary>A stable, machine-readable error code (see <c>MemoryErrorCodes</c>), when <see cref="Status"/> is not <see cref="IngestionItemStatus.Succeeded"/>.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// A diagnostic message, when applicable. Must not include prompts, complete memory contents,
    /// secrets, connection strings, or provider credentials.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Whether retrying this item (e.g. via fail-fast + re-submission) is expected to be safe/useful.</summary>
    public bool Retryable { get; init; }
}
