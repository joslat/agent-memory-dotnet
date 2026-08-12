namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// The turns an extractor should extract from, plus the earlier turns it may read to understand them
/// (E2).
/// </summary>
/// <remarks>
/// <para>
/// Extraction has always seen one batch — typically a user turn and the reply — which is often not
/// enough to resolve what was said. "I moved there last year" needs the turn that named the place;
/// "she recommended it" needs the turn that named her. Widening the batch fixes the reference and
/// creates a worse problem, which is why this type exists rather than a bigger <c>messages</c> list.
/// </para>
/// <para>
/// <b>Context is not a target, and the distinction is now a correctness property rather than a
/// question of token efficiency.</b> Re-extracting the preceding turns re-asserts facts already
/// stored. That used to be merely wasteful — the exact triple MERGEs, so nothing was corrupted — but
/// with confidence reinforcement (S2) a re-assertion earns α, and the salience reranker (R7) reads a
/// <c>mention_count</c> the MERGE increments. If context turns were extracted as targets, a fact
/// would gain confidence and mentions every time it happened to sit inside a sliding window, so both
/// signals would measure <i>how recently something was said</i> rather than <i>how often the world
/// asserted it</i>. Corroboration would quietly become recency.
/// </para>
/// <para>
/// So the contract is: read <see cref="Context"/> to disambiguate, extract only from
/// <see cref="Targets"/>, and attribute provenance only to <see cref="Targets"/>.
/// </para>
/// </remarks>
public sealed record ExtractionWindow
{
    /// <summary>The turns to extract memories from. Provenance is attributed here and nowhere else.</summary>
    public required IReadOnlyList<Message> Targets { get; init; }

    /// <summary>
    /// Earlier turns, in order, supplied purely so the targets can be understood.
    /// </summary>
    /// <remarks>
    /// Nothing may be extracted from these. They carry no provenance and must never appear in
    /// <c>SourceMessageIds</c>: an <c>EXTRACTED_FROM</c> edge to a context turn would claim the fact
    /// was stated there, which is exactly the turn the extractor was told not to extract from.
    /// </remarks>
    public IReadOnlyList<Message> Context { get; init; } = [];

    /// <summary>A window with no context — identical in effect to the pre-E2 <c>ExtractAsync(messages)</c>.</summary>
    public static ExtractionWindow ForTargets(IReadOnlyList<Message> targets) =>
        new() { Targets = targets };

    /// <summary><see langword="true"/> when there is context to render at all.</summary>
    public bool HasContext => Context.Count > 0;
}
