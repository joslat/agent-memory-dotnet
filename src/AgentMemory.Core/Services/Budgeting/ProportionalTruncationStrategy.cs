using AgentMemory.Abstractions.Options;

namespace AgentMemory.Core.Services.Budgeting;

/// <summary>
/// <see cref="TruncationStrategy.Proportional"/> — shrink every section (and the GraphRAG block) by the same
/// fraction <c>maxChars / totalChars</c>, keeping the longest in-budget prefix of each. Preserves the
/// relative mix of sources rather than removing items globally.
/// </summary>
internal sealed class ProportionalTruncationStrategy : ITruncationStrategy
{
    public TruncationStrategy Strategy => TruncationStrategy.Proportional;

    public TruncationResult Truncate(TruncationInput input)
    {
        double ratio = (double)input.MaxCharacters / input.TotalCharacters;

        var trimmedRecent = ContextBudgetEstimator.TrimToRatio(input.Recent, ContextBudgetEstimator.EstimateChars(input.Recent), ratio);
        var trimmedRelevant = ContextBudgetEstimator.TrimToRatio(input.Relevant, ContextBudgetEstimator.EstimateChars(input.Relevant), ratio);
        var trimmedEntities = ContextBudgetEstimator.TrimToRatio(input.Entities, ContextBudgetEstimator.EstimateChars(input.Entities), ratio);
        var trimmedPreferences = ContextBudgetEstimator.TrimToRatio(input.Preferences, ContextBudgetEstimator.EstimateChars(input.Preferences), ratio);
        var trimmedFacts = ContextBudgetEstimator.TrimToRatio(input.Facts, ContextBudgetEstimator.EstimateChars(input.Facts), ratio);
        var trimmedTraces = ContextBudgetEstimator.TrimToRatio(input.Traces, ContextBudgetEstimator.EstimateChars(input.Traces), ratio);

        string? trimmedGraphRag = input.GraphRag is null
            ? null
            : ContextBudgetEstimator.TruncateToCharBudget(input.GraphRag, (int)(input.GraphRag.Length * ratio));

        return new TruncationResult(trimmedRecent, trimmedRelevant, trimmedEntities,
            trimmedPreferences, trimmedFacts, trimmedTraces, trimmedGraphRag);
    }
}
