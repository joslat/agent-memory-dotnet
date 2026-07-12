using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Extraction.Llm.Internal;

namespace AgentMemory.Extraction.Llm;

/// <summary>
/// Extracts user preferences from conversation messages using an LLM.
/// </summary>
internal sealed class LlmPreferenceExtractor : ExtractorBase<ExtractedPreference>, IPreferenceExtractor
{
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

    private readonly LlmExtractionOptions _options;
    private readonly LlmExtractionRunner _runner;

    public LlmPreferenceExtractor(
        IChatClient chatClient,
        IOptions<LlmExtractionOptions> options,
        ILogger<LlmPreferenceExtractor> logger)
        : base(logger)
    {
        _options = options.Value;
        _runner = new LlmExtractionRunner(chatClient, _options, logger);
    }

    protected override async Task<IReadOnlyList<ExtractedPreference>> ExtractCoreAsync(
        IReadOnlyList<Message> messages, CancellationToken ct)
    {
        var conversationText = ConversationTextBuilder.Build(messages);
        return await _runner.RunAsync(
            _options.PreferenceExtractionPrompt ?? DefaultSystemPrompt,
            "Extract preferences from this conversation:",
            conversationText,
            ProjectPreferences,
            ct).ConfigureAwait(false);
    }

    private static IReadOnlyList<ExtractedPreference> ProjectPreferences(LlmExtractionResponse dto)
    {
        if (dto.Preferences is null) return Array.Empty<ExtractedPreference>();

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
}
