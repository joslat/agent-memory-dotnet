namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Strategy for merging results from multiple extractors.
/// </summary>
public enum MergeStrategyType
{
    /// <summary>Combine all results, deduplicate by name/key.</summary>
    Union,

    /// <summary>Only keep results found by ALL extractors.</summary>
    Intersection,

    /// <summary>Keep highest-confidence result per entity/fact.</summary>
    Confidence,

    /// <summary>Try extractors in order, use first that returns results.</summary>
    Cascade,

    /// <summary>Use the first extractor that doesn't throw.</summary>
    FirstSuccess
}
