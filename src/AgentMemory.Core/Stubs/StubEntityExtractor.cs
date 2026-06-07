using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Core.Stubs;

/// <summary>
/// Phase 1 stub: returns no entities. Replace in Phase 2 with an AI-backed extractor.
/// </summary>
public sealed class StubEntityExtractor : IEntityExtractor
{
    private readonly ILogger<StubEntityExtractor> _logger;

    /// <summary>Initializes a new instance of the <see cref="StubEntityExtractor"/> class.</summary>
    /// <param name="logger">The logger used to record diagnostic information.</param>
    public StubEntityExtractor(ILogger<StubEntityExtractor> logger) => _logger = logger;

    /// <inheritdoc/>
    public Task<IReadOnlyList<ExtractedEntity>> ExtractAsync(
        IReadOnlyList<Message> messages,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("StubEntityExtractor is in use — returning empty entity list.");
        return Task.FromResult<IReadOnlyList<ExtractedEntity>>(Array.Empty<ExtractedEntity>());
    }
}
