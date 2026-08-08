using AgentMemory.Abstractions.Options;

namespace AgentMemory.AgentFramework.Recall;

/// <summary>
/// The outcome of <see cref="IAutomaticRecallPolicy.DecideAsync"/> (#88).
/// </summary>
public sealed record AutomaticRecallDecision
{
    /// <summary>When <see langword="false"/>, no recall is performed and this turn injects no memory context.</summary>
    public bool ShouldRecall { get; init; } = true;

    /// <summary>
    /// Which memory categories to query. Default (<see cref="AutomaticRecallCategories.All"/>) queries
    /// every category the host has configured a nonzero limit for -- i.e. it changes nothing versus the
    /// pre-#88 behavior. Excluding a category zeroes its <c>RecallOptions.MaxX</c> limit so it is not
    /// queried at all, unless <see cref="RecallOptions"/> is also set (which takes precedence).
    /// </summary>
    public AutomaticRecallCategories Categories { get; init; } = AutomaticRecallCategories.All;

    /// <summary>
    /// Per-turn ranking-intent override (D3). Null leaves the host's configured
    /// <c>RecallOptions.Intent</c> untouched; a non-null value overrides it for this turn only, unless
    /// <see cref="RecallOptions"/> is also set (which takes precedence).
    /// </summary>
    public RankingIntent? Intent { get; init; }

    /// <summary>
    /// The question needs a relation returned <i>whole</i>, not merely its most similar members.
    /// </summary>
    /// <remarks>
    /// Top-K is a relevance cutoff and gives no completeness guarantee, so an aggregation question is
    /// unanswerable from it: miss one of five matching facts and the count is four, with nothing
    /// signalling the loss. Setting this turns on predicate expansion and query-relation resolution
    /// for the turn.
    /// <para>
    /// It is a routed decision rather than a global default because it is expensive — expansion
    /// roughly triples the retrieved context — and because it only helps the questions that need
    /// completeness. Measured: 73.3% to 90.0% on the questions it applies to, at a cost every other
    /// turn would pay for nothing.
    /// </para>
    /// </remarks>
    public bool RequiresRelationCompleteness { get; init; }

    /// <summary>
    /// An explicit, complete override of the effective <see cref="Abstractions.Options.RecallOptions"/> for
    /// this turn. When set, <see cref="Categories"/> and <see cref="Intent"/> above are ignored -- this
    /// value is used verbatim (its <c>Scope</c> is still always cleared, the same invariant as before #88).
    /// Null (default) derives the effective options from the host's configured <c>RecallOptions</c> plus
    /// <see cref="Categories"/>/<see cref="Intent"/> instead.
    /// </summary>
    public RecallOptions? RecallOptions { get; init; }

    /// <summary>Recall everything the host has configured, unchanged (the pre-#88 behavior).</summary>
    public static AutomaticRecallDecision Recall { get; } = new();

    /// <summary>Skip automatic recall entirely for this turn.</summary>
    public static AutomaticRecallDecision Skip { get; } = new() { ShouldRecall = false };
}
