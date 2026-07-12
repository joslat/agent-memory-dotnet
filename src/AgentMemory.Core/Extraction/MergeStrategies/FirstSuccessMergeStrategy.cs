using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Core.Extraction.MergeStrategies;

/// <summary>
/// Same as cascade but specifically designed for error tolerance:
/// returns the first non-empty result list, treating empty lists as "no success".
/// In the multi-extractor pipeline, failed extractors return empty lists,
/// so this effectively uses the first extractor that didn't throw.
/// </summary>
internal sealed class FirstSuccessMergeStrategy<T> : IMergeStrategy<T> where T : class
{
    /// <inheritdoc/>
    public MergeStrategyType StrategyType => MergeStrategyType.FirstSuccess;

    /// <inheritdoc/>
    public IReadOnlyList<T> Merge(IReadOnlyList<IReadOnlyList<T>> extractorResults)
    {
        foreach (var resultList in extractorResults)
        {
            if (resultList.Count > 0)
                return resultList;
        }

        return Array.Empty<T>();
    }
}
