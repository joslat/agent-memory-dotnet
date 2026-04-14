using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Core.Extraction.MergeStrategies;

/// <summary>
/// Combines all results from every extractor, deduplicating by a normalized key.
/// For entities: case-insensitive name. For facts: (subject, predicate, object) triple.
/// When duplicates exist, keeps the one with the highest confidence.
/// </summary>
public sealed class UnionMergeStrategy<T> : IMergeStrategy<T> where T : class
{
    private readonly Func<T, string> _keySelector;
    private readonly Func<T, double> _confidenceSelector;

    public UnionMergeStrategy(Func<T, string> keySelector, Func<T, double> confidenceSelector)
    {
        _keySelector = keySelector;
        _confidenceSelector = confidenceSelector;
    }

    public MergeStrategyType StrategyType => MergeStrategyType.Union;

    public IReadOnlyList<T> Merge(IReadOnlyList<IReadOnlyList<T>> extractorResults)
    {
        var best = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);

        foreach (var resultList in extractorResults)
        {
            foreach (var item in resultList)
            {
                var key = _keySelector(item);
                if (!best.TryGetValue(key, out var existing) ||
                    _confidenceSelector(item) > _confidenceSelector(existing))
                {
                    best[key] = item;
                }
            }
        }

        return best.Values.ToList().AsReadOnly();
    }
}
