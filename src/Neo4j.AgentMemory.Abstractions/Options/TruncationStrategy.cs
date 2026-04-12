namespace Neo4j.AgentMemory.Abstractions.Options;

/// <summary>
/// Strategy for truncating context when budget is exceeded.
/// </summary>
public enum TruncationStrategy
{
    /// <summary>
    /// Remove oldest items first.
    /// </summary>
    OldestFirst,

    /// <summary>
    /// Remove lowest-scoring items first.
    /// </summary>
    LowestScoreFirst,

    /// <summary>
    /// Proportionally reduce each section.
    /// </summary>
    Proportional,

    /// <summary>
    /// Fail if budget is exceeded.
    /// </summary>
    Fail
}
