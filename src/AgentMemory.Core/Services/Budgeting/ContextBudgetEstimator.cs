using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Services.Budgeting;

/// <summary>
/// Character-cost estimation primitives shared by <see cref="MemoryContextAssembler"/> and the
/// <see cref="ITruncationStrategy"/> implementations. These are pure functions over the assembled
/// memory sections; the numbers are deliberately cheap approximations (≈4 chars per token) used only
/// to keep an assembled context within its configured budget, never for billing or exact accounting.
/// </summary>
internal static class ContextBudgetEstimator
{
    /// <summary>Sum of the estimated character cost of every item in a section.</summary>
    internal static int EstimateChars<T>(IReadOnlyList<T> items) =>
        items.Sum(EstimateItemChars);

    /// <summary>Estimated character cost of a single domain item.</summary>
    internal static int EstimateItemChars<T>(T item) => item switch
    {
        // Every item costs at least 1 char so that budget truncation always makes forward progress —
        // an empty-content message/preference must still reduce the running total when removed,
        // otherwise the victim loop could churn through zero-cost items without nearing the budget.
        Message m => Math.Max(1, m.Content.Length),
        Entity e => (e.Name?.Length ?? 0) + (e.Description?.Length ?? 0) + 10,
        Fact f => f.Subject.Length + f.Predicate.Length + f.Object.Length + 4,
        Preference p => Math.Max(1, p.PreferenceText.Length),
        ReasoningTrace t => t.Task.Length + (t.Outcome?.Length ?? 0) + 10,
        _ => 50
    };

    /// <summary>
    /// Keep the longest prefix of <paramref name="items"/> whose combined estimated cost does not exceed
    /// <c>currentChars * ratio</c>. Used by the proportional truncation strategy to shrink each section
    /// by the same fraction.
    /// </summary>
    internal static IReadOnlyList<T> TrimToRatio<T>(IReadOnlyList<T> items, int currentChars, double ratio)
    {
        if (items.Count == 0) return items;
        int targetChars = (int)(currentChars * ratio);
        int runningChars = 0;
        int keepCount = 0;
        foreach (var item in items)
        {
            int itemChars = EstimateItemChars(item);
            if (runningChars + itemChars > targetChars) break;
            runningChars += itemChars;
            keepCount++;
        }
        return items.Take(keepCount).ToList();
    }

    /// <summary>
    /// Truncates <paramref name="text"/> to at most <paramref name="budget"/> UTF-16 char units without
    /// splitting a surrogate pair (a non-BMP character such as an emoji occupies 2 char units; slicing
    /// between them would emit an orphaned surrogate). Backs the cut off by one when it lands on a low
    /// surrogate.
    /// </summary>
    internal static string TruncateToCharBudget(string text, int budget)
    {
        if (budget >= text.Length) return text;
        if (budget <= 0) return string.Empty;
        // If the cut index sits on the low (second) half of a surrogate pair, back off so the pair stays whole.
        if (char.IsLowSurrogate(text[budget])) budget--;
        return text[..budget];
    }
}
