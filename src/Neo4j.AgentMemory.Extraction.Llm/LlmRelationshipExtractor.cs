using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Extraction.Llm.Internal;

namespace Neo4j.AgentMemory.Extraction.Llm;

/// <summary>
/// Extracts relationships between entities from conversation messages using an LLM.
/// </summary>
public sealed class LlmRelationshipExtractor : IRelationshipExtractor
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
    private readonly ILogger<LlmRelationshipExtractor> _logger;

    public LlmRelationshipExtractor(
        IChatClient chatClient,
        IOptions<LlmExtractionOptions> options,
        ILogger<LlmRelationshipExtractor> logger)
    {
        _chatClient = chatClient;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ExtractedRelationship>> ExtractAsync(
        IReadOnlyList<Message> messages,
        CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0)
            return Array.Empty<ExtractedRelationship>();

        var conversationText = BuildConversationText(messages);

        try
        {
            var chatMessages = new List<ChatMessage>
            {
                new(ChatRole.System, SystemPrompt),
                new(ChatRole.User, $"Extract relationships from this conversation:\n\n{conversationText}")
            };

            var chatOptions = BuildChatOptions();
            var response = await _chatClient.GetResponseAsync(chatMessages, chatOptions, cancellationToken);
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Relationship extraction failed; returning empty list.");
            return Array.Empty<ExtractedRelationship>();
        }
    }

    private ChatOptions BuildChatOptions()
    {
        var opts = new ChatOptions { Temperature = _options.Temperature };
        if (!string.IsNullOrEmpty(_options.ModelId))
            opts.ModelId = _options.ModelId;
        return opts;
    }

    private static string BuildConversationText(IReadOnlyList<Message> messages) =>
        string.Join("\n", messages.Select(m => $"{m.Role}: {m.Content}"));
}
