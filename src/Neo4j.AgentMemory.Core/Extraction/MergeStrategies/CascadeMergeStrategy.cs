using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Core.Extraction.MergeStrategies;

/// <summary>
/// Tries each extractor's results in order and returns the first non-empty list.
/// If all lists are empty, returns an empty list.
/// </summary>
public sealed class CascadeMergeStrategy<T> : IMergeStrategy<T> where T : class
{
    public MergeStrategyType StrategyType => MergeStrategyType.Cascade;

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
