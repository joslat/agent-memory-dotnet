using Microsoft.Extensions.Logging;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Core.Stubs;

/// <summary>
/// Phase 1 stub pipeline: orchestrates all four stub extractors and returns an empty but structurally
/// correct ExtractionResult. Replace individual extractors in Phase 2 with AI-backed implementations.
/// </summary>
public sealed class StubExtractionPipeline : IMemoryExtractionPipeline
{
    private readonly IEntityExtractor _entityExtractor;
    private readonly IFactExtractor _factExtractor;
    private readonly IPreferenceExtractor _preferenceExtractor;
    private readonly IRelationshipExtractor _relationshipExtractor;
    private readonly ILogger<StubExtractionPipeline> _logger;

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

    public async Task<ExtractionResult> ExtractAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("StubExtractionPipeline processing {MessageCount} messages for session {SessionId}.",
            request.Messages.Count, request.SessionId);

        var types = request.TypesToExtract;

        var entities = types.HasFlag(ExtractionTypes.Entities)
            ? await _entityExtractor.ExtractAsync(request.Messages, cancellationToken)
            : Array.Empty<ExtractedEntity>();

        var facts = types.HasFlag(ExtractionTypes.Facts)
            ? await _factExtractor.ExtractAsync(request.Messages, cancellationToken)
            : Array.Empty<ExtractedFact>();

        var preferences = types.HasFlag(ExtractionTypes.Preferences)
            ? await _preferenceExtractor.ExtractAsync(request.Messages, cancellationToken)
            : Array.Empty<ExtractedPreference>();

        var relationships = types.HasFlag(ExtractionTypes.Relationships)
            ? await _relationshipExtractor.ExtractAsync(request.Messages, cancellationToken)
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
