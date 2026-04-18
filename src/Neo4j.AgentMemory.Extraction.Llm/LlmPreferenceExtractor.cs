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
/// Extracts user preferences from conversation messages using an LLM.
/// </summary>
public sealed class LlmPreferenceExtractor : ExtractorBase<ExtractedPreference>, IPreferenceExtractor
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    public const string DefaultSystemPrompt =
        """
        You are a preference extraction assistant. Identify user preferences, likes, dislikes,
        and stated requirements from the conversation.
        Return JSON only — no markdown, no explanation.

        Output format:
        {"preferences": [{"category": "...", "preference": "...", "context": "optional context or null", "confidence": 0.85}]}

        Category examples: communication_style, technology, food, work_habits, language, tools, format

        Guidelines:
        - Focus on the user's expressed or implied preferences
        - Confidence: 0.9 for explicit statements ("I prefer"), 0.75 for inferred preferences
        - Include the context that supports the preference
        - Return {"preferences": []} if nothing found
        """;

    private readonly IChatClient _chatClient;
    private readonly LlmExtractionOptions _options;

    public LlmPreferenceExtractor(
        IChatClient chatClient,
        IOptions<LlmExtractionOptions> options,
        ILogger<LlmPreferenceExtractor> logger)
        : base(logger)
    {
        _chatClient = chatClient;
        _options = options.Value;
    }

    protected override async Task<IReadOnlyList<ExtractedPreference>> ExtractCoreAsync(
        IReadOnlyList<Message> messages, CancellationToken ct)
    {
        var conversationText = ConversationTextBuilder.Build(messages);

        var chatMessages = new List<ChatMessage>
        {
            new(ChatRole.System, _options.PreferenceExtractionPrompt ?? DefaultSystemPrompt),
            new(ChatRole.User, $"Extract preferences from this conversation:\n\n{conversationText}")
        };

        var chatOptions = BuildChatOptions();
        var response = await _chatClient.GetResponseAsync(chatMessages, chatOptions, ct);
        var json = response.Text;

        var dto = JsonSerializer.Deserialize<LlmExtractionResponse>(json ?? "", JsonOptions);
        if (dto?.Preferences is null)
            return Array.Empty<ExtractedPreference>();

        return dto.Preferences
            .Where(p => !string.IsNullOrWhiteSpace(p.Category)
                     && !string.IsNullOrWhiteSpace(p.Preference))
            .Select(p => new ExtractedPreference
            {
                Category = p.Category,
                PreferenceText = p.Preference,
                Context = string.IsNullOrWhiteSpace(p.Context) ? null : p.Context,
                Confidence = p.Confidence
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
