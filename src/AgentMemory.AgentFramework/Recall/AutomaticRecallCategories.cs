namespace AgentMemory.AgentFramework.Recall;

/// <summary>
/// Selects which memory categories an <see cref="IAutomaticRecallPolicy"/> wants queried for the current
/// turn (#88). Excluding a category from <see cref="AutomaticRecallDecision.Categories"/> zeroes its
/// corresponding <c>RecallOptions.MaxX</c> limit, so <c>MemoryContextAssembler</c> skips that category's
/// retrieval entirely instead of querying it and discarding the result.
/// </summary>
[Flags]
public enum AutomaticRecallCategories
{
    /// <summary>No automatic recall category is queried.</summary>
    None = 0,

    /// <summary>Recent short-term messages for this session.</summary>
    RecentMessages = 1 << 0,

    /// <summary>Semantically relevant short-term messages for this session.</summary>
    RelevantMessages = 1 << 1,

    /// <summary>Long-term entity nodes.</summary>
    Entities = 1 << 2,

    /// <summary>Long-term fact nodes.</summary>
    Facts = 1 << 3,

    /// <summary>Long-term preference nodes.</summary>
    Preferences = 1 << 4,

    /// <summary>Reasoning-trace nodes. Excluded from <see cref="Default"/> -- opt in explicitly.</summary>
    ReasoningTraces = 1 << 5,

    /// <summary>GraphRAG context. Excluded from <see cref="Default"/> -- opt in explicitly.</summary>
    GraphRag = 1 << 6,

    /// <summary>
    /// A sensible baseline for a policy that wants "the usual" categories without reasoning traces or
    /// GraphRAG: <see cref="RecentMessages"/> | <see cref="RelevantMessages"/> | <see cref="Entities"/> |
    /// <see cref="Facts"/> | <see cref="Preferences"/>.
    /// </summary>
    Default = RecentMessages | RelevantMessages | Entities | Facts | Preferences,

    /// <summary>
    /// Every category. This is <see cref="AutomaticRecallDecision"/>'s own default -- it reproduces the
    /// complete configured recall exactly as before #88, deferring entirely to whatever the host already
    /// configured for reasoning traces / GraphRAG rather than silently disabling them.
    /// </summary>
    All = RecentMessages | RelevantMessages | Entities | Facts | Preferences | ReasoningTraces | GraphRag
}
