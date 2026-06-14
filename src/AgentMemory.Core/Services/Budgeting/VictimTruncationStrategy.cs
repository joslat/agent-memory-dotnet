using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Core.Services.Budgeting;

/// <summary>
/// Base for the "remove one worst item at a time until within budget" strategies. Concrete subclasses
/// decide which item is "worst" via <see cref="IsWorse"/>; GraphRAG (a single opaque string) is dropped
/// only once every section item has been removed.
/// </summary>
internal abstract class VictimTruncationStrategy : ITruncationStrategy
{
    /// <inheritdoc/>
    public abstract TruncationStrategy Strategy { get; }

    /// <summary>True when <paramref name="candidate"/> should be removed before <paramref name="current"/>.</summary>
    protected abstract bool IsWorse(Victim candidate, Victim current);

    /// <summary>A removal candidate: identifies one item in one section plus the keys used to rank it.</summary>
    protected readonly record struct Victim(int Section, int Index, int Chars, DateTimeOffset Timestamp, double ScoreRank);

    public TruncationResult Truncate(TruncationInput input)
    {
        var recentList = input.Recent.ToList();
        var relevantList = input.Relevant.ToList();
        var entityList = input.Entities.ToList();
        var prefList = input.Preferences.ToList();
        var factList = input.Facts.ToList();
        var traceList = input.Traces.ToList();
        var graphRag = input.GraphRag;

        int totalChars = ContextBudgetEstimator.EstimateChars(recentList) + ContextBudgetEstimator.EstimateChars(relevantList)
            + ContextBudgetEstimator.EstimateChars(entityList) + ContextBudgetEstimator.EstimateChars(prefList)
            + ContextBudgetEstimator.EstimateChars(factList) + ContextBudgetEstimator.EstimateChars(traceList)
            + (graphRag?.Length ?? 0);

        while (totalChars > input.MaxCharacters)
        {
            var victim = SelectVictim(recentList, relevantList, entityList, prefList, factList, traceList);
            if (victim is null)
            {
                // No section items remain — drop the GraphRAG block last.
                if (graphRag != null) { totalChars -= graphRag.Length; graphRag = null; continue; }
                break;
            }

            totalChars -= victim.Value.Chars;
            RemoveVictim(victim.Value, recentList, relevantList, entityList, prefList, factList, traceList);
        }

        return new TruncationResult(recentList, relevantList, entityList, prefList, factList, traceList, graphRag);
    }

    private Victim? SelectVictim(
        List<Message> recent,
        List<Message> relevant,
        List<Entity> entities,
        List<Preference> preferences,
        List<Fact> facts,
        List<ReasoningTrace> traces)
    {
        Victim? worst = null;

        void Consider(int section, int index, int count, int chars, DateTimeOffset timestamp)
        {
            // Sections arrive best-score-first; map position to a [0,1] relevance proxy
            // (1.0 = best in section, 0.0 = worst). A singleton is treated as best-in-section.
            double scoreRank = count <= 1 ? 1.0 : (double)(count - 1 - index) / (count - 1);
            var candidate = new Victim(section, index, chars, timestamp, scoreRank);
            if (worst is null || IsWorse(candidate, worst.Value))
                worst = candidate;
        }

        for (int i = 0; i < recent.Count; i++) Consider(0, i, recent.Count, ContextBudgetEstimator.EstimateItemChars(recent[i]), recent[i].TimestampUtc);
        for (int i = 0; i < relevant.Count; i++) Consider(1, i, relevant.Count, ContextBudgetEstimator.EstimateItemChars(relevant[i]), relevant[i].TimestampUtc);
        for (int i = 0; i < entities.Count; i++) Consider(2, i, entities.Count, ContextBudgetEstimator.EstimateItemChars(entities[i]), entities[i].CreatedAtUtc);
        for (int i = 0; i < preferences.Count; i++) Consider(3, i, preferences.Count, ContextBudgetEstimator.EstimateItemChars(preferences[i]), preferences[i].CreatedAtUtc);
        for (int i = 0; i < facts.Count; i++) Consider(4, i, facts.Count, ContextBudgetEstimator.EstimateItemChars(facts[i]), facts[i].CreatedAtUtc);
        for (int i = 0; i < traces.Count; i++) Consider(5, i, traces.Count, ContextBudgetEstimator.EstimateItemChars(traces[i]), traces[i].StartedAtUtc);

        return worst;
    }

    private static void RemoveVictim(
        Victim victim,
        List<Message> recent,
        List<Message> relevant,
        List<Entity> entities,
        List<Preference> preferences,
        List<Fact> facts,
        List<ReasoningTrace> traces)
    {
        switch (victim.Section)
        {
            case 0: recent.RemoveAt(victim.Index); break;
            case 1: relevant.RemoveAt(victim.Index); break;
            case 2: entities.RemoveAt(victim.Index); break;
            case 3: preferences.RemoveAt(victim.Index); break;
            case 4: facts.RemoveAt(victim.Index); break;
            case 5: traces.RemoveAt(victim.Index); break;
        }
    }
}

/// <summary>
/// <see cref="TruncationStrategy.OldestFirst"/> — remove the globally oldest item (by timestamp) across all
/// sections first; ties broken by lowest relevance.
/// </summary>
internal sealed class OldestFirstTruncationStrategy : VictimTruncationStrategy
{
    public override TruncationStrategy Strategy => TruncationStrategy.OldestFirst;

    protected override bool IsWorse(Victim candidate, Victim current) =>
        candidate.Timestamp < current.Timestamp
        || (candidate.Timestamp == current.Timestamp && candidate.ScoreRank < current.ScoreRank);
}

/// <summary>
/// <see cref="TruncationStrategy.LowestScoreFirst"/> — remove the globally lowest-relevance item first, using
/// each item's rank within its repo-sorted (best-score-first) section as the score proxy; ties broken by
/// oldest timestamp.
/// </summary>
internal sealed class LowestScoreFirstTruncationStrategy : VictimTruncationStrategy
{
    public override TruncationStrategy Strategy => TruncationStrategy.LowestScoreFirst;

    protected override bool IsWorse(Victim candidate, Victim current) =>
        candidate.ScoreRank < current.ScoreRank
        || (candidate.ScoreRank == current.ScoreRank && candidate.Timestamp < current.Timestamp);
}
