using Neo4j.AgentMemory.Abstractions.Domain;

namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Strategy for merging results from multiple extractors.
/// </summary>
/// <typeparam name="T">The extraction result type.</typeparam>
public interface IMergeStrategy<T> where T : class
{
    /// <summary>The strategy type this implementation handles.</summary>
    MergeStrategyType StrategyType { get; }

    /// <summary>
    /// Merges multiple extractor result lists into a single deduplicated list.
    /// </summary>
    /// <param name="extractorResults">One result list per extractor.</param>
    IReadOnlyList<T> Merge(IReadOnlyList<IReadOnlyList<T>> extractorResults);
}
