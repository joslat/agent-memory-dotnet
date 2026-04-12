using Microsoft.Extensions.Logging;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Core.Stubs;

/// <summary>
/// Phase 1 stub: returns the input entity unchanged with no deduplication. Replace in Phase 2.
/// </summary>
public sealed class StubEntityResolver : IEntityResolver
{
    private readonly ILogger<StubEntityResolver> _logger;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;

    public StubEntityResolver(
        ILogger<StubEntityResolver> logger,
        IClock clock,
        IIdGenerator idGenerator)
    {
        _logger = logger;
        _clock = clock;
        _idGenerator = idGenerator;
    }

    public Task<Entity> ResolveEntityAsync(
        ExtractedEntity extractedEntity,
        IReadOnlyList<string> sourceMessageIds,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("StubEntityResolver is in use — returning new entity without deduplication.");

        var entity = new Entity
        {
            EntityId = _idGenerator.GenerateId(),
            Name = extractedEntity.Name,
            CanonicalName = extractedEntity.Name,
            Type = extractedEntity.Type,
            Subtype = extractedEntity.Subtype,
            Description = extractedEntity.Description,
            Confidence = extractedEntity.Confidence,
            Aliases = extractedEntity.Aliases,
            Attributes = extractedEntity.Attributes,
            SourceMessageIds = sourceMessageIds,
            CreatedAtUtc = _clock.UtcNow
        };

        return Task.FromResult(entity);
    }

    public Task<IReadOnlyList<Entity>> FindPotentialDuplicatesAsync(
        string name,
        string type,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("StubEntityResolver is in use — returning empty duplicate list.");
        return Task.FromResult<IReadOnlyList<Entity>>(Array.Empty<Entity>());
    }
}
