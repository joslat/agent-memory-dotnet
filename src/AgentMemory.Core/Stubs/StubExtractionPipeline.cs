using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Core.Stubs;

/// <summary>
/// Default extraction pipeline: orchestrates the four registered extractors (entity, fact,
/// preference, relationship) — honoring <see cref="ExtractionRequest.TypesToExtract"/> — and
/// aggregates their output into a single <see cref="ExtractionResult"/>. With AI-backed extractors
/// registered (the default) this performs full extraction; the legacy type name is retained for
/// source compatibility.
/// </summary>
internal sealed class StubExtractionPipeline : IMemoryExtractionPipeline
{
    private readonly IEntityExtractor _entityExtractor;
    private readonly IFactExtractor _factExtractor;
    private readonly IPreferenceExtractor _preferenceExtractor;
    private readonly IRelationshipExtractor _relationshipExtractor;
    private readonly ILogger<StubExtractionPipeline> _logger;

    /// <summary>Initializes a new instance of the <see cref="StubExtractionPipeline"/> class.</summary>
    public StubExtractionPipeline(
        IEntityExtractor entityExtractor,
        IFactExtractor factExtractor,
        IPreferenceExtractor preferenceExtractor,
        IRelationshipExtractor relationshipExtractor,
        ILogger<StubExtractionPipeline> logger)
    {
        _entityExtractor = entityExtractor;
        _factExtractor = factExtractor;
        _preferenceExtractor = preferenceExtractor;
        _relationshipExtractor = relationshipExtractor;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<ExtractionResult> ExtractAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("StubExtractionPipeline processing {MessageCount} messages for session {SessionId}.",
            request.Messages.Count, request.SessionId);

        var types = request.TypesToExtract;

        var entities = types.HasFlag(ExtractionTypes.Entities)
            ? await _entityExtractor.ExtractAsync(request.Messages, cancellationToken).ConfigureAwait(false)
            : Array.Empty<ExtractedEntity>();

        var facts = types.HasFlag(ExtractionTypes.Facts)
            ? await _factExtractor.ExtractAsync(request.Messages, cancellationToken).ConfigureAwait(false)
            : Array.Empty<ExtractedFact>();

        var preferences = types.HasFlag(ExtractionTypes.Preferences)
            ? await _preferenceExtractor.ExtractAsync(request.Messages, cancellationToken).ConfigureAwait(false)
            : Array.Empty<ExtractedPreference>();

        var relationships = types.HasFlag(ExtractionTypes.Relationships)
            ? await _relationshipExtractor.ExtractAsync(request.Messages, cancellationToken).ConfigureAwait(false)
            : Array.Empty<ExtractedRelationship>();

        var sourceIds = request.Messages
            .Select(m => m.MessageId)
            .ToList();

        return new ExtractionResult
        {
            Entities = entities,
            Facts = facts,
            Preferences = preferences,
            Relationships = relationships,
            SourceMessageIds = sourceIds,
            Metadata = new Dictionary<string, object>
            {
                ["stub"] = true,
                ["sessionId"] = request.SessionId
            }
        };
    }
}
