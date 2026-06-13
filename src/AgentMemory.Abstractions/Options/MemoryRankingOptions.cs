namespace AgentMemory.Abstractions.Options;

/// <summary>
/// Per-request query intent (D3) that re-weights retrieval ranking for a single recall, applied over the
/// configured <see cref="MemoryRankingOptions"/>. Set via <c>RecallOptions.Intent</c>.
/// </summary>
public enum RankingIntent
{
    /// <summary>Use the configured ranking weights as-is.</summary>
    Default = 0,

    /// <summary>Favour fresh memories — raise the recency weight ("what's the latest?").</summary>
    Latest = 1,

    /// <summary>
    /// Favour structurally/semantically similar memories regardless of age — zero recency, so an
    /// old-but-relevant precedent is not buried (analog / case-based retrieval). Structural decay still applies.
    /// </summary>
    Analog = 2,
}

/// <summary>
/// Capability tier for the memory system — a "start at parity, dial up" switch. Each profile supplies
/// sensible defaults for the opt-in retrieval-ranking knobs in <see cref="MemoryRankingOptions"/> (an
/// explicitly-set knob always overrides the profile). The physical Neo4j schema is identical across
/// profiles: <see cref="Parity"/> and <see cref="Enhanced"/> differ only in read-time ranking and add no
/// property, label, or index — so a profile can be raised over an existing graph with no migration.
/// </summary>
public enum MemoryProfile
{
    /// <summary>
    /// Upstream-equivalent behaviour: rank purely by semantic (vector) score — no recency or structural
    /// re-ranking. The default; zero behaviour change, compatible with the canonical Python schema.
    /// </summary>
    Parity = 0,

    /// <summary>
    /// Adds the schema-neutral relevance enhancements: recency re-ranking (the already-computed ACT-R
    /// retention score blended into ranking) and structural hop-decay in graph traversal. No schema change.
    /// </summary>
    Enhanced = 1,

    /// <summary>
    /// <see cref="Enhanced"/> ranking plus (future) bitemporal / non-destructive-decay storage behaviour.
    /// Today its ranking defaults match <see cref="Enhanced"/>; the storage tier (invalidate-not-delete,
    /// two-clock recall) lands in later phases.
    /// </summary>
    Bitemporal = 2,
}

/// <summary>
/// Read-time retrieval-ranking configuration (opt-in, schema-neutral). Controls how long-term vector
/// recall and GraphRAG traversal order their results. By default (<see cref="MemoryProfile.Parity"/>)
/// ranking is purely semantic (vector score) — today's behaviour. Raising the <see cref="Profile"/> (or
/// setting an individual knob) blends in recency and structural-distance signals the system already
/// computes. None of these change the Neo4j schema.
/// </summary>
public sealed record MemoryRankingOptions
{
    /// <summary>
    /// Capability tier supplying defaults for the knobs below; an explicitly-set knob overrides it.
    /// Default <see cref="MemoryProfile.Parity"/> ⇒ recency weight 0 and γ 1.0 ⇒ semantic-only ranking.
    /// </summary>
    public MemoryProfile Profile { get; init; } = MemoryProfile.Parity;

    /// <summary>
    /// D1 — weight of the recency/retention term when blending it with the semantic score:
    /// <c>blended = (1 − w)·vectorScore + w·retentionScore</c>, <c>w ∈ [0,1]</c>. 0 ⇒ off (semantic only).
    /// Null ⇒ derive from <see cref="Profile"/>. The retention term reuses the ACT-R decay curve
    /// (<see cref="MemoryDecayOptions"/>) and is clamped to [0,1] before blending.
    /// </summary>
    public double? RecencyWeight { get; init; }

    /// <summary>
    /// D2 — per-hop structural decay <c>γ ∈ (0,1]</c> for GraphRAG traversal: a neighbour at <c>h</c> hops
    /// is scored <c>seedScore · γ^h</c>. 1.0 ⇒ off (no distance decay; today's ordering). Null ⇒ derive
    /// from <see cref="Profile"/>.
    /// </summary>
    public double? StructuralDecayGamma { get; init; }

    /// <summary>Effective recency weight after applying the profile default and clamping to [0,1].</summary>
    public double EffectiveRecencyWeight => Clamp01(RecencyWeight ?? ProfileRecency(Profile));

    /// <summary>Effective structural-decay γ after the profile default; clamped to (0,1] (out-of-range ⇒ off).</summary>
    public double EffectiveStructuralDecayGamma
    {
        get
        {
            var g = StructuralDecayGamma ?? ProfileGamma(Profile);
            if (g <= 0) return 1.0;          // 0 / negative is meaningless for γ^hops ⇒ treat as off
            return g > 1.0 ? 1.0 : g;
        }
    }

    /// <summary>Whether the recency re-ranker is active (effective weight &gt; 0).</summary>
    public bool RecencyRerankEnabled => EffectiveRecencyWeight > 0;

    /// <summary>Whether structural hop-decay is active (effective γ &lt; 1).</summary>
    public bool StructuralDecayEnabled => EffectiveStructuralDecayGamma < 1.0;

    /// <summary>
    /// Returns a per-request variant adjusted for a query <see cref="RankingIntent"/> (D3), over these
    /// configured options: <see cref="RankingIntent.Latest"/> raises the recency weight (favour fresh);
    /// <see cref="RankingIntent.Analog"/> zeroes it (favour structurally/semantically similar regardless of
    /// age — precedent retrieval); <see cref="RankingIntent.Default"/> is unchanged. Structural decay (γ) is
    /// left untouched.
    /// </summary>
    public MemoryRankingOptions ForIntent(RankingIntent intent) => intent switch
    {
        RankingIntent.Latest => this with { RecencyWeight = Math.Max(EffectiveRecencyWeight, 0.6) },
        RankingIntent.Analog => this with { RecencyWeight = 0.0 },
        _ => this,
    };

    /// <summary>Default (parity) instance — semantic-only ranking.</summary>
    public static MemoryRankingOptions Default { get; } = new();

    private static double ProfileRecency(MemoryProfile p) => p switch
    {
        MemoryProfile.Enhanced or MemoryProfile.Bitemporal => 0.3,
        _ => 0.0,
    };

    private static double ProfileGamma(MemoryProfile p) => p switch
    {
        MemoryProfile.Enhanced or MemoryProfile.Bitemporal => 0.7,
        _ => 1.0,
    };

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
