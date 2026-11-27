namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// An untrusted claim, made by a store-aware writer, that a mention names a stored entity the writer was shown in the
/// same call. It is model output, not identity proof: only evidence-based identity reads it, re-checks it against its
/// own bounded candidates, vetoes and the current source, and lets the entity type's policy decide whether it may
/// confirm or only propose. Every other path ignores it.
/// </summary>
public sealed record ExtractedEntityReference
{
    /// <summary>The stored entity's ID, mapped from a label the writer was shown; never a model-invented ID.</summary>
    public required string EntityId { get; init; }

    /// <summary>The exact, contiguous words of the current message the reference rests on.</summary>
    public required string Quote { get; init; }

    /// <summary>
    /// True when the writer did not state the reference itself: a fact it wrote names the stored entity by its exact
    /// stored name. Evidence-based identity keeps an implied reference only when its checks confirm it; otherwise the
    /// mention is dropped, with no proposal and no adjudication, and the fact stays as it was.
    /// </summary>
    public bool Implied { get; init; }
}
