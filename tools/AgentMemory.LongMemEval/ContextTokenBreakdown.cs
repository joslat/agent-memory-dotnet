using AgentMemory.Abstractions.Domain;

namespace AgentMemory.LongMemEval;

/// <summary>
/// What one section of an assembled context costs in tokens (B3).
/// </summary>
/// <param name="Section">Section name, as it appears on <see cref="MemoryContext"/>.</param>
/// <param name="Items">How many memories the section contributed.</param>
/// <param name="Tokens">Their cost, counted with the real tokenizer.</param>
internal sealed record ContextSectionTokens(string Section, int Items, int Tokens);

/// <summary>
/// The token cost of answering from memory versus from the whole conversation (B3).
/// </summary>
/// <remarks>
/// <para>
/// <b>The most persuasive number in this category is a claim about architecture, not model quality.</b>
/// "1.6k context tokens instead of 115k" cannot be inflated by a better answer model, cannot be
/// tuned by prompt engineering, and does not move when the judge changes its mind. It is either true
/// of the context that was assembled or it is not.
/// </para>
/// <para>
/// Which is exactly why it has to be counted rather than estimated. Until C4 this codebase converted
/// tokens to characters by multiplying by four; publishing a compression ratio derived from that
/// would have been a claim about arithmetic dressed up as a claim about design.
/// </para>
/// <para>
/// The baseline is deliberately the <b>full transcript</b> — what a memoryless agent would have to
/// send to answer the same question. Comparing against a truncated window instead would flatter the
/// result by measuring against a system that has already given up on remembering.
/// </para>
/// </remarks>
internal sealed record ContextTokenBreakdown
{
    /// <summary>Per-section costs, largest first.</summary>
    internal required IReadOnlyList<ContextSectionTokens> Sections { get; init; }

    /// <summary>Total tokens in the assembled memory context.</summary>
    internal int ContextTokens => Sections.Sum(s => s.Tokens);

    /// <summary>Tokens in the full conversation the memory was built from.</summary>
    internal required int FullHistoryTokens { get; init; }

    /// <summary>How the counting was done, so a reader can tell a real count from a fallback.</summary>
    /// <remarks>
    /// Recorded rather than assumed. <see cref="LongMemEvalTokenCounter"/> falls back to an estimate
    /// when it cannot resolve an encoding for the model, and a compression ratio computed from an
    /// estimate must not be readable as a measurement.
    /// </remarks>
    internal required string CountMethod { get; init; }

    /// <summary>The encoding used, when one was resolved.</summary>
    internal string? Encoding { get; init; }

    /// <summary>
    /// How many times smaller the memory context is than the full history.
    /// </summary>
    /// <remarks>
    /// Null when there is no history to compare against — an empty denominator is not a ratio of
    /// infinity, it is an absent measurement, and reporting a number there would be the single most
    /// flattering way to be wrong.
    /// </remarks>
    internal double? CompressionRatio =>
        FullHistoryTokens == 0 ? null : (double)FullHistoryTokens / Math.Max(1, ContextTokens);

    /// <summary>
    /// Measures an assembled context against the conversation it was distilled from.
    /// </summary>
    internal static ContextTokenBreakdown Measure(
        MemoryContext context,
        IReadOnlyList<Message> fullHistory,
        LongMemEvalTokenCounter counter)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(fullHistory);
        ArgumentNullException.ThrowIfNull(counter);

        var sections = new List<ContextSectionTokens>
        {
            Section("RecentMessages", context.RecentMessages.Items, m => m.Content, counter),
            Section("RelevantMessages", context.RelevantMessages.Items, m => m.Content, counter),
            Section("RelevantEntities", context.RelevantEntities.Items,
                e => $"{e.Name} {e.Type} {e.Description}", counter),
            Section("RelevantFacts", context.RelevantFacts.Items,
                f => $"{f.Subject} {f.Predicate} {f.Object}", counter),
            Section("RelevantPreferences", context.RelevantPreferences.Items,
                p => $"{p.Category} {p.PreferenceText}", counter),
            Section("SimilarTraces", context.SimilarTraces.Items,
                t => $"{t.Task} {t.Outcome}", counter),
        };

        return new ContextTokenBreakdown
        {
            // Largest first: the point of a breakdown is to show where the budget actually goes, and
            // a fixed section order buries that behind whichever section happens to be listed first.
            Sections = sections.OrderByDescending(s => s.Tokens).ToList(),
            FullHistoryTokens = fullHistory.Sum(m => counter.Count(m.Content)),
            CountMethod = counter.Method.ToString(),
            Encoding = counter.Encoding,
        };
    }

    private static ContextSectionTokens Section<T>(
        string name, IReadOnlyList<T> items, Func<T, string> render, LongMemEvalTokenCounter counter) =>
        new(name, items.Count, items.Sum(item => counter.Count(render(item))));
}
