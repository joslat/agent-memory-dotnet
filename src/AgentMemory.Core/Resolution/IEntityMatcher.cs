using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Resolution;

internal interface IEntityMatcher
{
    EntityMatchType MatchType { get; }

    Task<EntityResolutionResult?> TryMatchAsync(
        ExtractedEntity candidate,
        IReadOnlyList<Entity> existingEntities,
        CancellationToken cancellationToken = default);
}
