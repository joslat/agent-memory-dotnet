using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Core.Extraction.MergeStrategies;

/// <summary>
/// When duplicate items are found across extractors, keeps the one with the highest confidence.
/// All unique items are preserved. Effectively a union with confidence-based winner selection.
/// </summary>
public sealed class ConfidenceMergeStrategy<T> : IMergeStrategy<T> where T : class
{
    private readonly Func<T, string> _keySelector;
    private readonly Func<T, double> _confidenceSelector;

    public ConfidenceMergeStrategy(Func<T, string> keySelector, Func<T, double> confidenceSelector)
    {
        _keySelector = keySelector;
        _confidenceSelector = confidenceSelector;
    }

    public MergeStrategyType StrategyType => MergeStrategyType.Confidence;

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
