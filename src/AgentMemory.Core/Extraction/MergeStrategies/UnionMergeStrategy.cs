using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Core.Extraction.MergeStrategies;

/// <summary>
/// Combines all results from every extractor, deduplicating by a normalized key.
/// For entities: case-insensitive name. For facts: (subject, predicate, object) triple.
/// When duplicates exist, keeps the one with the highest confidence.
/// </summary>
public sealed class UnionMergeStrategy<T> : IMergeStrategy<T> where T : class
{
    private readonly Func<T, string> _keySelector;
    private readonly Func<T, double> _confidenceSelector;

    /// <summary>
    /// Initializes a new instance of the <see cref="UnionMergeStrategy{T}"/> class.
    /// </summary>
    /// <param name="keySelector">Selects the normalized deduplication key for an item.</param>
    /// <param name="confidenceSelector">Selects the confidence score used to choose between duplicates.</param>
    public UnionMergeStrategy(Func<T, string> keySelector, Func<T, double> confidenceSelector)
    {
        _keySelector = keySelector;
        _confidenceSelector = confidenceSelector;
    }

    /// <inheritdoc/>
    public MergeStrategyType StrategyType => MergeStrategyType.Union;

    /// <inheritdoc/>
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
