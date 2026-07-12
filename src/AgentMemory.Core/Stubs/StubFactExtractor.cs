using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Core.Stubs;

/// <summary>
/// Phase 1 stub: returns no facts. Replace in Phase 2 with an AI-backed extractor.
/// </summary>
internal sealed class StubFactExtractor : IFactExtractor
{
    private readonly ILogger<StubFactExtractor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StubFactExtractor"/> class.
    /// </summary>
    /// <param name="logger">The logger used to record diagnostic information.</param>
    public StubFactExtractor(ILogger<StubFactExtractor> logger) => _logger = logger;

    /// <inheritdoc/>
    public Task<IReadOnlyList<ExtractedFact>> ExtractAsync(
        IReadOnlyList<Message> messages,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("StubFactExtractor is in use — returning empty fact list.");
        return Task.FromResult<IReadOnlyList<ExtractedFact>>(Array.Empty<ExtractedFact>());
    }
}
