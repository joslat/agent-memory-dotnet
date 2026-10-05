namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// G4 (PLAN 40.48): what a successful write did to the store, on <see cref="IngestionItemOutcome.Effect"/>.
/// </summary>
public enum MemoryWriteEffect
{
    /// <summary>Not reported for this item (an entity: resolution decides it and does not say so yet).</summary>
    Unreported = 0,

    /// <summary>A new memory was written.</summary>
    Created = 1,

    /// <summary>The memory was already stored (the same fact, preference or relationship); nothing new was written.</summary>
    AlreadyStored = 2,

    /// <summary>The item said the same as another item of this extraction and was folded into it (a skipped outcome).</summary>
    MergedWithinExtraction = 3,
}
