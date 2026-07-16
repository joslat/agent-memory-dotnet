namespace AgentMemory.Abstractions.Domain;

/// <summary>Per-item outcome status recorded in an <see cref="IngestionItemOutcome"/> (#101).</summary>
public enum IngestionItemStatus
{
    /// <summary>The item completed this stage successfully.</summary>
    Succeeded,

    /// <summary>The item was deliberately not processed further (e.g. failed validation, or a
    /// relationship whose endpoint was never resolved/persisted) -- not an error, but worth surfacing.</summary>
    Skipped,

    /// <summary>The item failed this stage.</summary>
    Failed
}
