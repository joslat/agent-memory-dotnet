using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Core.Extraction.MergeStrategies;

/// <summary>
/// Only keeps items found by 2 or more extractors (matched by normalized key).
/// When duplicates exist, keeps the one with the highest confidence.
/// </summary>
public sealed class IntersectionMergeStrategy<T> : IMergeStrategy<T> where T : class
{
    private readonly Func<T, string> _keySelector;
    private readonly Func<T, double> _confidenceSelector;

    public IntersectionMergeStrategy(Func<T, string> keySelector, Func<T, double> confidenceSelector)
    {
        _keySelector = keySelector;
        _confidenceSelector = confidenceSelector;
    }

    public MergeStrategyType StrategyType => MergeStrategyType.Intersection;

    public IReadOnlyList<T> Merge(IReadOnlyList<IReadOnlyList<T>> extractorResults)
    {
        if (extractorResults.Count == 0)
            return Array.Empty<T>();

        // Count how many extractors produced each key, keeping the best item per key.
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var best = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);

        foreach (var resultList in extractorResults)
        {
            // Track keys seen within this single extractor to avoid double-counting.
            var seenInExtractor = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in resultList)
            {
                var key = _keySelector(item);

                if (seenInExtractor.Add(key))
                {
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }

                if (!best.TryGetValue(key, out var existing) ||
                    _confidenceSelector(item) > _confidenceSelector(existing))
                {
                    best[key] = item;
                }
            }
        }

        return best
            .Where(kvp => counts.GetValueOrDefault(kvp.Key) >= 2)
            .Select(kvp => kvp.Value)
            .ToList()
            .AsReadOnly();
    }
}
