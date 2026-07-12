using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Core.Stubs;

/// <summary>
/// Phase 1 stub: returns no relationships. Replace in Phase 2 with an AI-backed extractor.
/// </summary>
internal sealed class StubRelationshipExtractor : IRelationshipExtractor
{
    private readonly ILogger<StubRelationshipExtractor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StubRelationshipExtractor"/> class.
    /// </summary>
    /// <param name="logger">The logger used to record diagnostic information.</param>
    public StubRelationshipExtractor(ILogger<StubRelationshipExtractor> logger) => _logger = logger;

    /// <inheritdoc/>
    public Task<IReadOnlyList<ExtractedRelationship>> ExtractAsync(
        IReadOnlyList<Message> messages,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("StubRelationshipExtractor is in use — returning empty relationship list.");
        return Task.FromResult<IReadOnlyList<ExtractedRelationship>>(Array.Empty<ExtractedRelationship>());
    }
}
