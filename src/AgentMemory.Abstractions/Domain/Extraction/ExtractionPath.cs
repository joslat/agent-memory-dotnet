namespace AgentMemory.Abstractions.Domain;

/// <summary>The extraction path actually attempted for a result, independent of ingestion success.</summary>
public enum ExtractionPath
{
    /// <summary>The implementation did not report a path; never infer one from configuration.</summary>
    Unknown = 0,
    /// <summary>No extraction was needed, for example because the novelty gate skipped the turn.</summary>
    Skipped,
    /// <summary>The store-aware writer was attempted, without an extractor fallback.</summary>
    Writer,
    /// <summary>The configured extractors were attempted directly, including imports.</summary>
    Extractors,
    /// <summary>The writer failed and extractors were attempted. Inspect ingestion status for their outcome.</summary>
    WriterFallback,
    /// <summary>A previously extracted result was processed, including multi-session batch extraction.</summary>
    PreExtracted,
}
