using FuzzySharp;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Options;

namespace Neo4j.AgentMemory.Core.Resolution;

/// <summary>
/// Matches entities using FuzzySharp token-sort ratio (equivalent to Python's token_sort_ratio).
/// Returns 0.0–1.0 confidence (FuzzySharp returns 0–100, divided by 100).
/// </summary>
internal sealed class FuzzyMatchEntityMatcher : IEntityMatcher
{
    private readonly EntityResolutionOptions _options;

    public FuzzyMatchEntityMatcher(EntityResolutionOptions options)
    {
        _options = options;
    }

    public string MatchType => "fuzzy";

    public Task<EntityResolutionResult?> TryMatchAsync(
        ExtractedEntity candidate,
        IReadOnlyList<Entity> existingEntities,
        CancellationToken cancellationToken = default)
    {
        var thresholdScore = (int)(_options.FuzzyMatchThreshold * 100);
        var candidateName = candidate.Name;

        Entity? bestEntity = null;
        int bestScore = thresholdScore - 1; // Must beat threshold to qualify

        foreach (var existing in existingEntities)
        {
            var score = BestScoreAgainst(candidateName, existing);
            if (score > bestScore)
            {
                bestScore = score;
                bestEntity = existing;
            }
        }

        if (bestEntity is null)
            return Task.FromResult<EntityResolutionResult?>(null);

        var result = new EntityResolutionResult
        {
            ResolvedEntity = bestEntity,
            MatchType = MatchType,
            Confidence = bestScore / 100.0
        };
        return Task.FromResult<EntityResolutionResult?>(result);
    }

    private static int BestScoreAgainst(string candidateName, Entity existing)
    {
        var best = Fuzz.TokenSortRatio(candidateName, existing.Name);

        if (existing.CanonicalName is not null)
        {
            var s = Fuzz.TokenSortRatio(candidateName, existing.CanonicalName);
            if (s > best) best = s;
        }

        foreach (var alias in existing.Aliases)
        {
            var s = Fuzz.TokenSortRatio(candidateName, alias);
            if (s > best) best = s;
        }

        return best;
    }
}
