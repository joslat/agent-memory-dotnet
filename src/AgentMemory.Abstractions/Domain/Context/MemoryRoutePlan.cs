namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// The four routing decisions one recall made, in one place (PLAN 40.20): whether and how much to recall, how time was
/// read, whether the question was split, and how the result was fitted to the budget.
/// </summary>
/// <remarks>
/// Each decision was already made, and partly recorded, in a different place: the recall caps in the options, the time
/// reading in the temporal query parser, the split in the fan-out report, the fit in truncation. This puts them side by
/// side so a trace (the <c>memory.route.plan</c> event on <c>memory.recall.total</c>) and a host showing "why this answer"
/// read one record. It reports what happened; it decides nothing.
/// </remarks>
public sealed record MemoryRoutePlan
{
    /// <summary>Token for a recall made against now.</summary>
    public const string TimeNow = "now";

    /// <summary>Token for a recall at a moment the question itself named ("where did I live in March?").</summary>
    public const string TimeFromQuestion = "question";

    /// <summary>Token for a recall at a moment the caller asked for (<c>RecallAsOfAsync</c>).</summary>
    public const string TimeRequested = "requested";

    /// <summary>Whether and how much: the items asked for per memory kind (0 = that kind is not recalled).</summary>
    public required IReadOnlyDictionary<string, int> Recall { get; init; }

    /// <summary>
    /// How time was read, as a stable token: <see cref="TimeNow"/> (<c>now</c>), <see cref="TimeFromQuestion"/>
    /// (<c>question</c>: the question named a moment) or <see cref="TimeRequested"/> (<c>requested</c>: the caller asked
    /// for one).
    /// </summary>
    public required string Time { get; init; }

    /// <summary>The valid-time moment recalled, when not now.</summary>
    public DateTimeOffset? ValidAsOf { get; init; }

    /// <summary>The transaction-time moment ("as known then"), when not now.</summary>
    public DateTimeOffset? KnownAsOf { get; init; }

    /// <summary>Whether the question was split into sub-queries (fan-out).</summary>
    public bool Split { get; init; }

    /// <summary>The fan-out rules that fired, when it was split.</summary>
    public IReadOnlyList<string> SplitRules { get; init; } = [];

    /// <summary>How many sub-queries ran.</summary>
    public int SubQueries { get; init; }

    /// <summary>The context budget's token limit (null: none).</summary>
    public int? BudgetMaxTokens { get; init; }

    /// <summary>The context budget's character limit (null: none).</summary>
    public int? BudgetMaxCharacters { get; init; }

    /// <summary>Whether anything was cut to fit.</summary>
    public bool Truncated { get; init; }
}
