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

    /// <summary>
    /// IBP-2, first step (<c>--identity-confirm-places-things</c>): the identity arms open places and things to the
    /// adjudicator's quoted confirmation (<see cref="AgentMemory.Abstractions.Options.IdentityResolutionOptions.ConfirmPlacesAndThings"/>).
    /// Off unless asked for, as in the library.
    /// </summary>
    public static bool ConfirmPlacesAndThings { get; set; }

    private static MemoryOptions Without(MemoryOptions options, bool lookAhead) =>
        options with { Recall = options.Recall with { Upcoming = lookAhead, TimeWindows = lookAhead, OverlapExpansion = lookAhead } };

    /// <summary>
    /// The judge sees at most 20 candidates: what a store larger than the scenario's leaves it, since the wide search
    /// takes the most similar first. With the scenario's few dozen facts the default 50 takes the whole store.
    /// </summary>
    private const int NarrowWideLimit = 20;

    private static MemoryOptions IdentityMemoryOptions()
    {
        var options = MemoryOptions.CreateRecommended();
        // ILC-13: coreference ("the cutover" after "the go-live") needs the turns just before; read-only context, never
        // extracted from. Four turns, as the option documents; the frozen gold reaches 12 of 13 same-session follow-ups at 3.
        options.Extraction.ExtractionContextTurns = 4;
        // REV4-1: recall for people is opt-in; the identity arms measure it.
        return options with { Recall = options.Recall with { EventDetails = true, PersonDetails = true } };
    }

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
        new(
            "graph-identity-unified", "identity-unified",
            "Explicit unified extractor + evidence-based identity + temporal recall; not Recommended; bounded validation only",
            IdentityMemoryOptions,
            o => { o.ApplyConversational(); o.UseMemoryWriter=false; o.MaxRetries=0; o.CaptureIdentityAliases=true; },
            o => o.ApplyRecommended(),
            Gate: null,
            Identity: o =>
            {
                o.Enabled=true; o.MaxCandidates=4; o.MaxAdjudicationsPerIngestion=2;
                o.AdjudicatorMaxOutputTokens=1024; o.AdjudicationTimeout=TimeSpan.FromSeconds(45);
                o.ConfirmPlacesAndThings = ConfirmPlacesAndThings;
                o.ApplyConversationalPolicies();
            }),
        new(
            "graph-legacy-writer", "legacy-writer",
            "Old model for the head-to-head: the same store-aware writer + DatesFromText + chat update-judge fallback, with the legacy similarity resolver; no evidence-based identity, no external retrieval judge",
            MemoryOptions.CreateRecommended,
            o => { o.ApplyRecommended(); o.MaxRetries=0; },
            o => o.ApplyRecommended(),
            Gate: null),
        new(
            "graph-identity-writer", "identity-writer",
            "Store-aware writer + DatesFromText + chat update-judge fallback + evidence-based identity; no external retrieval judge",
            IdentityMemoryOptions,
            o => { o.ApplyRecommended(); o.MaxRetries=0; o.CaptureIdentityAliases=true; o.MemoryWriterEventIdentities=true; },
            o => o.ApplyRecommended(),
            Gate: null,
            Identity: o =>
            {
                o.Enabled=true; o.MaxCandidates=4; o.MaxAdjudicationsPerIngestion=2;
                o.AdjudicatorMaxOutputTokens=1024; o.AdjudicationTimeout=TimeSpan.FromSeconds(45);
                o.ConfirmPlacesAndThings = ConfirmPlacesAndThings;
                o.ApplyConversationalPolicies();
            }),
        // IBP-1b: the freed entity places given back (RecallOptions.FillFoldedEntityPlaces), recalled from the same store as
        // the arm before it, so the setting is compared on the same memories. Never in a default run (opt-in by name).
        new(
            "graph-conversational-filled", "conversational",
            "Conversational's store and recall, with the entity places the fold frees given to the next entities (IBP-1b)",
            () => Filled(MemoryOptions.CreateConversational()),
            o => o.ApplyConversational(),
            o => o.ApplyConversational(),
            Gate: null),
        new(
            "graph-identity-writer-filled", "identity-writer",
            "The identity writer's store and recall, with the entity places the fold frees given to the next entities (IBP-1b)",
            () => Filled(IdentityMemoryOptions()),
            o => { o.ApplyRecommended(); o.MaxRetries=0; o.CaptureIdentityAliases=true; o.MemoryWriterEventIdentities=true; },
            o => o.ApplyRecommended(),
            Gate: null,
            Identity: o =>
            {
                o.Enabled=true; o.MaxCandidates=4; o.MaxAdjudicationsPerIngestion=2;
                o.AdjudicatorMaxOutputTokens=1024; o.AdjudicationTimeout=TimeSpan.FromSeconds(45);
                o.ConfirmPlacesAndThings = ConfirmPlacesAndThings;
                o.ApplyConversationalPolicies();
            }),
    ];

    private static MemoryOptions Filled(MemoryOptions options) => options with { Recall = options.Recall with { FillFoldedEntityPlaces = true } };

    public static IReadOnlyList<string> All => [.. Baselines, .. Graph.Select(arm => arm.Name)];

    /// <summary>
    /// ILC-21: the arm with one identity component switched off; null keeps it unchanged. IKB-TIER2-RERUN adds the one
    /// variant that switches something on: <c>namesake-filter-on</c>, RecallOptions.NamesakeFilter, measured before it can
    /// become a default.
    /// </summary>
    public static GraphArm Ablate(GraphArm arm, string? ablation) => ablation switch
    {
        null => arm,
        "namesake-filter-on" => arm with { Memory = () => { var m = arm.Memory(); return m with { Recall = m.Recall with { NamesakeFilter = true } }; } },
        "adjudicator-off" => arm with { Identity = o => { arm.Identity!(o); o.MaxAdjudicationsPerIngestion = 0; } },
        "context-turns-off" => arm with { Memory = () => { var m = arm.Memory(); m.Extraction.ExtractionContextTurns = 0; return m; } },
        "context-rooted-off" => arm with { Identity = o =>
        {
            arm.Identity!(o);
            foreach (var type in o.Policies.Keys.ToList()) o.Policies[type] = o.Policies[type] with { ReviewContextRootedCandidates = false };
        } },
        _ => throw new ArgumentException($"Unknown identity ablation '{ablation}'.", nameof(ablation)),
    };
}
