using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Core.Stubs;

/// <summary>
/// Phase 1 stub: returns the input entity unchanged with no deduplication. Replace in Phase 2.
/// </summary>
internal sealed class StubEntityResolver : IEntityResolver
{
    private readonly ILogger<StubEntityResolver> _logger;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;

    /// <summary>
    /// Initializes a new instance of the <see cref="StubEntityResolver"/> class.
    /// </summary>
    public StubEntityResolver(
        ILogger<StubEntityResolver> logger,
        IClock clock,
        IIdGenerator idGenerator)
    {
        _logger = logger;
        _clock = clock;
        _idGenerator = idGenerator;
    }

    /// <inheritdoc/>
    public Task<Entity> ResolveEntityAsync(
        ExtractedEntity extractedEntity,
        IReadOnlyList<string> sourceMessageIds,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("StubEntityResolver is in use — returning new entity without deduplication.");

        var entity = new Entity
        {
            EntityId = _idGenerator.GenerateId(),
            OwnerId = scope?.OwnerId, // R1 symmetry with CompositeEntityResolver (persistence re-stamps anyway)
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

    /// <inheritdoc/>
    public Task<IReadOnlyList<Entity>> FindPotentialDuplicatesAsync(
        string name,
        string type,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("StubEntityResolver is in use — returning empty duplicate list.");
        return Task.FromResult<IReadOnlyList<Entity>>(Array.Empty<Entity>());
    }
}
