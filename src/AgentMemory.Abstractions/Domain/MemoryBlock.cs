namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// One addressable line of a memory block (S4).
/// </summary>
/// <param name="MemoryId">The id of the underlying memory — what makes this line actionable.</param>
/// <param name="Kind">Entity, fact or preference.</param>
/// <param name="Text">The rendered line.</param>
public sealed record MemoryBlockLine(string MemoryId, MemoryItemKind Kind, string Text);

/// <summary>
/// A small, human-readable projection of what memory holds (S4).
/// </summary>
/// <remarks>
/// <para>
/// The honest assessment of this store was <i>"capable but opaque"</i>. A developer could not look at
/// an owner's memory and see it; they could query it, which is not the same thing. This is the view
/// that closes that gap.
/// </para>
/// <para>
/// <b>Rendered on demand and never stored.</b> That is the deliberate divergence from the
/// block-memory designs this borrows legibility from. A stored block is a second copy of the truth
/// that drifts from the graph the moment anything is written, and once an agent is allowed to edit
/// the block directly, the block — not the audited store — becomes where memory actually lives. Every
/// provenance edge, every trust level and every supersession record then describes a shadow of what
/// the system believes.
/// </para>
/// <para>
/// So this is a <b>read</b> surface. Changing memory goes through the ordinary write path, which
/// stamps trust, records provenance and leaves a history. To make that practical rather than
/// obstructive, each line carries its <see cref="MemoryBlockLine.MemoryId"/>: a human who spots
/// something wrong can act on that exact item instead of rewriting prose and hoping a parser agrees.
/// </para>
/// </remarks>
public sealed record MemoryBlock
{
    /// <summary>The owner this block describes; null = shared/global.</summary>
    public string? OwnerId { get; init; }

    /// <summary>When the block was rendered. It is a snapshot, and it says so.</summary>
    public required DateTimeOffset RenderedAtUtc { get; init; }

    /// <summary>The lines, in rendering order.</summary>
    public required IReadOnlyList<MemoryBlockLine> Lines { get; init; }

    /// <summary>
    /// How many memories were left out because the block was full.
    /// </summary>
    /// <remarks>
    /// <b>Counted and surfaced rather than silently dropped.</b> A block is a view a human trusts to
    /// show them what memory holds; one that quietly stops short reads as "this is everything" and is
    /// worse than no view at all, because it invites the conclusion that a missing memory was never
    /// stored.
    /// </remarks>
    public int OmittedCount { get; init; }

    /// <summary>Whether anything was left out.</summary>
    public bool IsTruncated => OmittedCount > 0;
}
