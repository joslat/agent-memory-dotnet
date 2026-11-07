using AgentMemory.Abstractions.Options;
using AgentMemory.Gate;
using AgentMemory.Scenarios.Memories;

namespace AgentMemory.Scenarios;

/// <summary>
/// The arms a run can compare: two line memories as baselines, and AgentMemory configured one way per arm. Arms that
/// share a store recall from what the first of them wrote, so a recall setting is compared on the same memories.
/// </summary>
internal static class Arms
{
    public static readonly IReadOnlyList<string> Baselines = ["file", "vector"];

    private static MemoryOptions Without(MemoryOptions options, bool lookAhead) =>
        options with { Recall = options.Recall with { Upcoming = lookAhead, TimeWindows = lookAhead, OverlapExpansion = lookAhead } };

    /// <summary>
    /// The judge sees at most 20 candidates: what a store larger than the scenario's leaves it, since the wide search
    /// takes the most similar first. With the scenario's few dozen facts the default 50 takes the whole store.
    /// </summary>
    private const int NarrowWideLimit = 20;

    public static readonly IReadOnlyList<GraphArm> Graph =
    [
        new(
            "graph-conversational", "conversational",
            "Conversational preset (frozen): the extractors write, similarity picks what reaches the prompt",
            MemoryOptions.CreateConversational,
            o => o.ApplyConversational(),
            o => o.ApplyConversational(),
            Gate: null),
        new(
            "graph-conversational-lookahead", "conversational",
            "Conversational's store, with look-ahead recall (upcoming, time windows, overlaps) and no judge",
            () => Without(MemoryOptions.CreateConversational(), lookAhead: true),
            o => o.ApplyConversational(),
            o => o.ApplyConversational(),
            Gate: null),
        new(
            "graph-recommended", "recommended",
            "Recommended preset: the store-aware writer with dates read into fields, a judge (JEV), look-ahead recall",
            MemoryOptions.CreateRecommended,
            o => o.ApplyRecommended(),
            o => o.ApplyRecommended(),
            Gate: _ => { }),
        new(
            "graph-recommended-no-lookahead", "recommended",
            "Recommended's store and judge, without look-ahead recall",
            () => Without(MemoryOptions.CreateRecommended(), lookAhead: false),
            o => o.ApplyRecommended(),
            o => o.ApplyRecommended(),
            Gate: _ => { }),
        new(
            "graph-recommended-narrow", "recommended",
            $"Recommended, the judge seeing {NarrowWideLimit} candidates as in a larger store, with look-ahead recall",
            MemoryOptions.CreateRecommended,
            o => o.ApplyRecommended(),
            o => o.ApplyRecommended(),
            Gate: g => g.WideLimit = NarrowWideLimit),
        new(
            "graph-recommended-narrow-no-lookahead", "recommended",
            $"Recommended, the judge seeing {NarrowWideLimit} candidates as in a larger store, without look-ahead recall",
            () => Without(MemoryOptions.CreateRecommended(), lookAhead: false),
            o => o.ApplyRecommended(),
            o => o.ApplyRecommended(),
            Gate: g => g.WideLimit = NarrowWideLimit),
    ];

    public static IReadOnlyList<string> All => [.. Baselines, .. Graph.Select(arm => arm.Name)];
}
