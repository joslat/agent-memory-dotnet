using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Core.Extraction.MergeStrategies;

/// <summary>
/// Only keeps items found by 2 or more extractors (matched by normalized key).
/// When duplicates exist, keeps the one with the highest confidence.
/// </summary>
internal sealed class IntersectionMergeStrategy<T> : IMergeStrategy<T> where T : class
{
    private readonly Func<T, string> _keySelector;
    private readonly Func<T, double> _confidenceSelector;

    /// <summary>Initializes a new instance of the <see cref="IntersectionMergeStrategy{T}"/> class.</summary>
    /// <param name="keySelector">Selects the normalized key used to match items across extractors.</param>
    /// <param name="confidenceSelector">Selects the confidence score used to pick the best item among duplicates.</param>
    public IntersectionMergeStrategy(Func<T, string> keySelector, Func<T, double> confidenceSelector)
    {
        _keySelector = keySelector;
        _confidenceSelector = confidenceSelector;
    }

    /// <inheritdoc/>
    public MergeStrategyType StrategyType => MergeStrategyType.Intersection;

    /// <inheritdoc/>
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
