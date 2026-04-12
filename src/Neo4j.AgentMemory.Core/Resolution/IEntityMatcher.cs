using Neo4j.AgentMemory.Abstractions.Domain;

namespace Neo4j.AgentMemory.Core.Resolution;

internal interface IEntityMatcher
{
    string MatchType { get; } // "exact", "fuzzy", "semantic"

    Task<EntityResolutionResult?> TryMatchAsync(
        ExtractedEntity candidate,
        IReadOnlyList<Entity> existingEntities,
        CancellationToken cancellationToken = default);
}
