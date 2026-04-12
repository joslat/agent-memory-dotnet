using Microsoft.Extensions.Logging;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Core.Stubs;

/// <summary>
/// Phase 1 stub: returns no facts. Replace in Phase 2 with an AI-backed extractor.
/// </summary>
public sealed class StubFactExtractor : IFactExtractor
{
    private readonly ILogger<StubFactExtractor> _logger;

    public StubFactExtractor(ILogger<StubFactExtractor> logger) => _logger = logger;

    public Task<IReadOnlyList<ExtractedFact>> ExtractAsync(
        IReadOnlyList<Message> messages,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("StubFactExtractor is in use — returning empty fact list.");
        return Task.FromResult<IReadOnlyList<ExtractedFact>>(Array.Empty<ExtractedFact>());
    }
}
