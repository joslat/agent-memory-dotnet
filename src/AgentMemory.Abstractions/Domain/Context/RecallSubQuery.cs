namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// One per-memory-type leg of a fanned-out recall (Proposal M, 30.10).
/// </summary>
/// <remarks>
/// The audit finding this exists to answer is that the mechanism was <b>unexpressible</b>: a caller
/// could ask one blended question and nothing else, so "split the query per memory type, retrieve
/// each against the real store, merge the retrieved contexts" could not be requested at all. This
/// type is the request-schema half of that.
/// </remarks>
public sealed record RecallSubQuery
{
    /// <summary>The memory type this leg targets.</summary>
    public required MemoryTypeAffinity Affinity { get; init; }

    /// <summary>The text to retrieve with — a rewritten fragment, not the caller's whole question.</summary>
    public required string QueryText { get; init; }

    /// <summary>
    /// A pre-computed embedding for <see cref="QueryText"/>. Null means the assembler embeds it.
    /// </summary>
    /// <remarks>
    /// Offered so a caller that already embedded its fragments does not pay twice, and so a test can
    /// drive the merge without an embedding provider at all.
    /// </remarks>
    public float[]? QueryEmbedding { get; init; }
}
