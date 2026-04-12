namespace Neo4j.AgentMemory.Abstractions.Options;

/// <summary>
/// Configuration for context budget limits.
/// </summary>
public sealed record ContextBudget
{
    /// <summary>Maximum total tokens for the assembled context.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>Maximum total characters for the assembled context.</summary>
    public int? MaxCharacters { get; init; }

    /// <summary>Truncation strategy when budget is exceeded.</summary>
    public TruncationStrategy TruncationStrategy { get; init; } = TruncationStrategy.OldestFirst;

    /// <summary>Default singleton instance with no limits.</summary>
    public static ContextBudget Default { get; } = new();
}
