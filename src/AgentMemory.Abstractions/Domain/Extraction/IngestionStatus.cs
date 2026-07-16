namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// Overall outcome of an ingestion (extraction + persistence) operation. See
/// <see cref="ExtractionResult.Status"/> (#101).
/// </summary>
public enum IngestionStatus
{
    /// <summary>Every attempted item succeeded (including the trivial case of nothing to ingest).</summary>
    Succeeded,

    /// <summary>At least one item succeeded and at least one item failed.</summary>
    PartiallySucceeded,

    /// <summary>At least one item was attempted and none succeeded.</summary>
    Failed
}
