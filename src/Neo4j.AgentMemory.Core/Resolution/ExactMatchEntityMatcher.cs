using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Options;

namespace Neo4j.AgentMemory.Core.Resolution;

/// <summary>
/// Matches entities using case-insensitive exact string equality on Name, CanonicalName, and Aliases.
/// </summary>
internal sealed class ExactMatchEntityMatcher : IEntityMatcher
{
    public string MatchType => "exact";

    public Task<EntityResolutionResult?> TryMatchAsync(
        ExtractedEntity candidate,
        IReadOnlyList<Entity> existingEntities,
        CancellationToken cancellationToken = default)
    {
        var candidateName = candidate.Name;

        foreach (var existing in existingEntities)
        {
            if (IsExactMatch(candidateName, existing))
            {
                var result = new EntityResolutionResult
                {
                    ResolvedEntity = existing,
                    MatchType = MatchType,
                    Confidence = 1.0
                };
                return Task.FromResult<EntityResolutionResult?>(result);
            }
        }

        return Task.FromResult<EntityResolutionResult?>(null);
    }

    private static bool IsExactMatch(string candidateName, Entity existing)
    {
        if (string.Equals(candidateName, existing.Name, StringComparison.OrdinalIgnoreCase))
            return true;

        if (existing.CanonicalName is not null &&
            string.Equals(candidateName, existing.CanonicalName, StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var alias in existing.Aliases)
        {
            if (string.Equals(candidateName, alias, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
