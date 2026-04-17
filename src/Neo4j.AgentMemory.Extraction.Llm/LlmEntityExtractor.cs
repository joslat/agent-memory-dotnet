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
/// Extracts named entities from conversation messages using an LLM.
/// </summary>
public sealed class LlmEntityExtractor : ExtractorBase<ExtractedEntity>, IEntityExtractor
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private const string SystemPrompt =
        """
        You are an entity extraction assistant. Extract named entities from the conversation.
        Return JSON only — no markdown, no explanation.

        Entity Types (POLE+O Model):
        - PERSON: Individuals by name or role
        - ORGANIZATION: Companies, groups, institutions
        - LOCATION: Places, addresses, geographic areas
        - EVENT: Incidents, meetings, occurrences
        - OBJECT: Physical or digital items, concepts, technologies

        Output format:
        {"entities": [{"name": "...", "type": "ENTITY_TYPE", "subtype": "optional or null", "description": "brief context", "confidence": 0.9, "aliases": ["alt_name"]}]}

        Guidelines:
        - Use UPPERCASE for type
        - Confidence: 0.95 for explicit mentions, 0.8 for inferred
        - Include aliases when mentioned
        - Do not extract pronouns or generic references
        - Return {"entities": []} if nothing found
        """;

    private readonly IChatClient _chatClient;
    private readonly LlmExtractionOptions _options;

    public LlmEntityExtractor(
        IChatClient chatClient,
        IOptions<LlmExtractionOptions> options,
        ILogger<LlmEntityExtractor> logger)
        : base(logger)
    {
        _chatClient = chatClient;
        _options = options.Value;
    }

    protected override async Task<IReadOnlyList<ExtractedEntity>> ExtractCoreAsync(
        IReadOnlyList<Message> messages, CancellationToken ct)
    {
        var conversationText = ConversationTextBuilder.Build(messages);

        var chatMessages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, $"Extract entities from this conversation:\n\n{conversationText}")
        };

        var chatOptions = BuildChatOptions();
        var response = await _chatClient.GetResponseAsync(chatMessages, chatOptions, ct);
        var json = response.Text;

        var dto = JsonSerializer.Deserialize<LlmExtractionResponse>(json ?? "", JsonOptions);
        if (dto?.Entities is null)
            return Array.Empty<ExtractedEntity>();

        return dto.Entities
            .Where(e => !string.IsNullOrWhiteSpace(e.Name) && !string.IsNullOrWhiteSpace(e.Type))
            .Select(e => new ExtractedEntity
            {
                Name = e.Name,
                Type = NormalizeEntityType(e.Type),
                Subtype = string.IsNullOrWhiteSpace(e.Subtype) ? null : e.Subtype,
                Description = string.IsNullOrWhiteSpace(e.Description) ? null : e.Description,
                Confidence = e.Confidence,
                Aliases = e.Aliases ?? new List<string>()
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

    private static string NormalizeEntityType(string type) => type.ToUpperInvariant() switch
    {
        "CONCEPT" => "OBJECT",
        "PLACE"   => "LOCATION",
        "COMPANY" => "ORGANIZATION",
        "INDIVIDUAL" => "PERSON",
        var t => t
    };
}
