namespace AgentMemory.Abstractions.Options;

/// <summary>
/// Controls how the extraction/persistence pipeline reacts to a per-item ingestion failure (#101).
/// </summary>
public enum IngestionFailureMode
{
    /// <summary>
    /// Continue processing independent items after a failure, collecting structured outcomes for
    /// each. The default -- appropriate for best-effort conversational memory, where one malformed
    /// item should not discard several otherwise-valid ones.
    /// </summary>
    BestEffort,

    /// <summary>
    /// Stop at the first non-recoverable ingestion failure and throw <c>MemoryIngestionException</c>,
    /// carrying every <c>IngestionItemOutcome</c> completed before the failure.
    /// </summary>
    FailFast
}
