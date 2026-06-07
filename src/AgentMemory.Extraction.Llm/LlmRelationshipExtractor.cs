using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Extraction.Llm.Internal;

namespace AgentMemory.Extraction.Llm;

/// <summary>
/// Extracts relationships between entities from conversation messages using an LLM.
/// </summary>
public sealed class LlmRelationshipExtractor : ExtractorBase<ExtractedRelationship>, IRelationshipExtractor
{
    public const string DefaultSystemPrompt =
        """
        You are a relationship extraction assistant. Identify relationships between named entities
        in the conversation.
        Return JSON only — no markdown, no explanation.

        Output format:
        {"relations": [{"source": "Entity A", "target": "Entity B", "relation_type": "RELATIONSHIP_TYPE", "description": "optional description or null", "confidence": 0.8}]}

        Relationship type examples: WORKS_AT, KNOWS, LOCATED_IN, PART_OF, OWNS, REPORTS_TO, CREATED_BY

        Guidelines:
        - Use UPPERCASE_SNAKE_CASE for relation_type
        - Only extract relationships between two named entities
        - Confidence: 0.9 for explicit statements, 0.7 for inferred
        - Return {"relations": []} if nothing found
        """;

    private readonly LlmExtractionOptions _options;
    private readonly LlmExtractionRunner _runner;

    public LlmRelationshipExtractor(
        IChatClient chatClient,
        IOptions<LlmExtractionOptions> options,
        ILogger<LlmRelationshipExtractor> logger)
        : base(logger)
    {
        _options = options.Value;
        _runner = new LlmExtractionRunner(chatClient, _options, logger);
    }

    protected override async Task<IReadOnlyList<ExtractedRelationship>> ExtractCoreAsync(
        IReadOnlyList<Message> messages, CancellationToken ct)
    {
        var conversationText = ConversationTextBuilder.Build(messages);
        return await _runner.RunAsync(
            _options.RelationshipExtractionPrompt ?? DefaultSystemPrompt,
            "Extract relationships from this conversation:",
            conversationText,
            ProjectRelationships,
            ct);
    }

    private static IReadOnlyList<ExtractedRelationship> ProjectRelationships(LlmExtractionResponse dto)
    {
        if (dto.Relations is null) return Array.Empty<ExtractedRelationship>();

        return dto.Relations
            .Where(r => !string.IsNullOrWhiteSpace(r.Source)
                     && !string.IsNullOrWhiteSpace(r.Target)
                     && !string.IsNullOrWhiteSpace(r.RelationType))
            .Select(r => new ExtractedRelationship
            {
                SourceEntity = r.Source,
                TargetEntity = r.Target,
                RelationshipType = r.RelationType,
                Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description,
                Confidence = r.Confidence
            })
            .ToList();
    }
}
