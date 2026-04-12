using Microsoft.Extensions.Logging;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Core.Stubs;

/// <summary>
/// Phase 1 stub: returns no preferences. Replace in Phase 2 with an AI-backed extractor.
/// </summary>
public sealed class StubPreferenceExtractor : IPreferenceExtractor
{
    private readonly ILogger<StubPreferenceExtractor> _logger;

    public StubPreferenceExtractor(ILogger<StubPreferenceExtractor> logger) => _logger = logger;

    public Task<IReadOnlyList<ExtractedPreference>> ExtractAsync(
        IReadOnlyList<Message> messages,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("StubPreferenceExtractor is in use — returning empty preference list.");
        return Task.FromResult<IReadOnlyList<ExtractedPreference>>(Array.Empty<ExtractedPreference>());
    }
}
