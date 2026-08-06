using AgentMemory.Abstractions.Options;

namespace AgentMemory.Neo4j.Infrastructure;

/// <summary>
/// Binds the D1 recency-rerank parameters (<c>$now</c>, <c>$lambda</c>, <c>$boostFactor</c>,
/// <c>$tmpWeight</c>) consumed by the <c>SearchByVector</c> blend. The decay curve (λ from the half-life,
/// the access-boost factor) is shared with the prune path via <see cref="MemoryDecayOptions"/>; the blend
/// weight comes from <see cref="MemoryRankingOptions.EffectiveRecencyWeight"/>. <c>$now</c> uses
/// <see cref="DateTimeOffset.UtcNow"/> to match the repositories' existing timestamp idiom.
/// </summary>
internal static class RerankParameters
{
    public static void Add(
        IDictionary<string, object?> parameters,
        MemoryRankingOptions ranking,
        MemoryDecayOptions decay)
    {
        // Guard a misconfigured half-life (≤0 ⇒ λ = ∞/NaN, which would poison ranking) by falling back
        // to the default curve; the decay service applies the same guard for the prune.
        double halfLife = decay.DecayHalfLifeDays > 0
            ? decay.DecayHalfLifeDays
            : MemoryDecayOptions.Default.DecayHalfLifeDays;

        parameters["now"] = DateTimeOffset.UtcNow.ToString("O");
        parameters["lambda"] = Math.Log(2) / halfLife;
        parameters["boostFactor"] = decay.AccessBoostFactor;
        parameters["maxBoost"] = decay.MaxAccessBoost;
        parameters["tmpWeight"] = ranking.EffectiveRecencyWeight;
    }
}
