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
/// Extracts Subject-Predicate-Object facts from conversation messages using an LLM.
/// </summary>
public sealed class LlmFactExtractor : ExtractorBase<ExtractedFact>, IFactExtractor
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private const string SystemPrompt =
        """
        You are a fact extraction assistant. Extract factual statements from the conversation.
        Return JSON only — no markdown, no explanation.

        A fact is a Subject-Predicate-Object triple:
        - Subject: the entity the fact is about
        - Predicate: the relationship or property (use snake_case verb phrases, e.g. "works_at", "is_located_in")
        - Object: the value or target entity

        Output format:
        {"facts": [{"subject": "...", "predicate": "...", "object": "...", "confidence": 0.9}]}

        Guidelines:
        - Only extract objective, verifiable facts; skip opinions
        - Confidence: 0.95 for explicitly stated facts, 0.75 for inferred
        - Return {"facts": []} if nothing found
        """;

    private readonly IChatClient _chatClient;
    private readonly LlmExtractionOptions _options;

    public LlmFactExtractor(
        IChatClient chatClient,
        IOptions<LlmExtractionOptions> options,
        ILogger<LlmFactExtractor> logger)
        : base(logger)
    {
        _chatClient = chatClient;
        _options = options.Value;
    }

    protected override async Task<IReadOnlyList<ExtractedFact>> ExtractCoreAsync(
        IReadOnlyList<Message> messages, CancellationToken ct)
    {
        var conversationText = ConversationTextBuilder.Build(messages);

        var chatMessages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, $"Extract facts from this conversation:\n\n{conversationText}")
        };

        var chatOptions = BuildChatOptions();
        var response = await _chatClient.GetResponseAsync(chatMessages, chatOptions, ct);
        var json = response.Text;

        var dto = JsonSerializer.Deserialize<LlmExtractionResponse>(json ?? "", JsonOptions);
        if (dto?.Facts is null)
            return Array.Empty<ExtractedFact>();

        return dto.Facts
            .Where(f => !string.IsNullOrWhiteSpace(f.Subject)
                     && !string.IsNullOrWhiteSpace(f.Predicate)
                     && !string.IsNullOrWhiteSpace(f.Object))
            .Select(f => new ExtractedFact
            {
                Subject = f.Subject,
                Predicate = f.Predicate,
                Object = f.Object,
                Confidence = f.Confidence
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
