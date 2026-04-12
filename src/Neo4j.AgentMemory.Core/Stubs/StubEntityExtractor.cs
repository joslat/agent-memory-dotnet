using Microsoft.Extensions.Logging;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Core.Stubs;

/// <summary>
/// Phase 1 stub: returns no entities. Replace in Phase 2 with an AI-backed extractor.
/// </summary>
public sealed class StubEntityExtractor : IEntityExtractor
{
    private readonly ILogger<StubEntityExtractor> _logger;

    public StubEntityExtractor(ILogger<StubEntityExtractor> logger) => _logger = logger;

    public Task<IReadOnlyList<ExtractedEntity>> ExtractAsync(
        IReadOnlyList<Message> messages,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("StubEntityExtractor is in use — returning empty entity list.");
        return Task.FromResult<IReadOnlyList<ExtractedEntity>>(Array.Empty<ExtractedEntity>());
    }
}
