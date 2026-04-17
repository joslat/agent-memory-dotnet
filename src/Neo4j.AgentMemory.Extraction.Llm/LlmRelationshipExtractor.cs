using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Core.Extraction;
using Neo4j.AgentMemory.Extraction.Llm.Internal;

namespace Neo4j.AgentMemory.Extraction.Llm;

/// <summary>
/// Extracts relationships between entities from conversation messages using an LLM.
/// </summary>
public sealed class LlmRelationshipExtractor : ExtractorBase<ExtractedRelationship>, IRelationshipExtractor
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private const string SystemPrompt =
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

    private readonly IChatClient _chatClient;
    private readonly LlmExtractionOptions _options;

    public LlmRelationshipExtractor(
        IChatClient chatClient,
        IOptions<LlmExtractionOptions> options,
        ILogger<LlmRelationshipExtractor> logger)
        : base(logger)
    {
        _chatClient = chatClient;
        _options = options.Value;
    }

    protected override async Task<IReadOnlyList<ExtractedRelationship>> ExtractCoreAsync(
        IReadOnlyList<Message> messages, CancellationToken ct)
    {
        var conversationText = ConversationTextBuilder.Build(messages);

        var chatMessages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, $"Extract relationships from this conversation:\n\n{conversationText}")
        };

        var chatOptions = BuildChatOptions();
        var response = await _chatClient.GetResponseAsync(chatMessages, chatOptions, ct);
        var json = response.Text;

        var dto = JsonSerializer.Deserialize<LlmExtractionResponse>(json ?? "", JsonOptions);
        if (dto?.Relations is null)
            return Array.Empty<ExtractedRelationship>();

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

    private ChatOptions BuildChatOptions()
    {
        var opts = new ChatOptions { Temperature = _options.Temperature };
        if (!string.IsNullOrEmpty(_options.ModelId))
            opts.ModelId = _options.ModelId;
        return opts;
    }
}
