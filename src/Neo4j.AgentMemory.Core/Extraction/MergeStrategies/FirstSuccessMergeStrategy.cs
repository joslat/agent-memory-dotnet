using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Core.Extraction.MergeStrategies;

/// <summary>
/// Same as cascade but specifically designed for error tolerance:
/// returns the first non-empty result list, treating empty lists as "no success".
/// In the multi-extractor pipeline, failed extractors return empty lists,
/// so this effectively uses the first extractor that didn't throw.
/// </summary>
public sealed class FirstSuccessMergeStrategy<T> : IMergeStrategy<T> where T : class
{
    public MergeStrategyType StrategyType => MergeStrategyType.FirstSuccess;

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
