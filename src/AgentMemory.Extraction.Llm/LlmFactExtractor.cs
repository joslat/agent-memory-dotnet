using AgentMemory.Abstractions.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Extraction.Llm.Internal;

namespace AgentMemory.Extraction.Llm;

/// <summary>
/// Extracts Subject-Predicate-Object facts from conversation messages using an LLM.
/// </summary>
internal sealed class LlmFactExtractor : ExtractorBase<ExtractedFact>, IFactExtractor
{
    public const string DefaultSystemPrompt =
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

    private readonly LlmExtractionOptions _options;
    private readonly LlmExtractionRunner _runner;

    public LlmFactExtractor(
        IChatClient chatClient,
        IOptions<LlmExtractionOptions> options,
        ILogger<LlmFactExtractor> logger)
        : base(logger)
    {
        _options = options.Value;
        _runner = new LlmExtractionRunner(chatClient, _options, logger);
    }

    protected override async Task<IReadOnlyList<ExtractedFact>> ExtractCoreAsync(
        IReadOnlyList<Message> messages, CancellationToken cancellationToken)
    {
        var conversationText = ConversationTextBuilder.Build(messages);
        return await _runner.RunAsync(
            _options.FactExtractionPrompt
                ?? BuildSystemPrompt(_options.AssistantContent, _options.TemporalValidity),
            "Extract facts from this conversation:",
            conversationText,
            ProjectFacts,
            cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<ExtractedFact> ProjectFacts(LlmExtractionResponse dto)
    {
        if (dto.Facts is null) return Array.Empty<ExtractedFact>();

        return dto.Facts
            .Where(f => !string.IsNullOrWhiteSpace(f.Subject)
                     && !string.IsNullOrWhiteSpace(f.Predicate)
                     && !string.IsNullOrWhiteSpace(f.Object))
            .Select(f => new ExtractedFact
            {
                Subject = f.Subject,
                Predicate = f.Predicate,
                Object = f.Object,
                Confidence = f.Confidence,
                ValidFrom = f.ValidFrom,
                ValidUntil = f.ValidUntil,
                SourceRole = f.SourceRole
            })
            .ToList();
    }

    /// <summary>The default prompt plus whatever the assistant-content setting asks for.</summary>
    /// <remarks>
    /// Shared with the two unified extractors through <see cref="ExtractionPromptSemantics"/>, so the
    /// three rungs of the extraction ladder cannot disagree about semantics again.
    /// </remarks>
    internal static string BuildSystemPrompt(AssistantContentMode assistantContent) =>
        BuildSystemPrompt(assistantContent, TemporalValidityMode.Ignore);

    /// <inheritdoc cref="BuildSystemPrompt(AssistantContentMode)"/>
    internal static string BuildSystemPrompt(
        AssistantContentMode assistantContent, TemporalValidityMode temporalValidity) =>
        DefaultSystemPrompt
        + ExtractionPromptSemantics.AssistantContentInstruction(assistantContent)
        + ExtractionPromptSemantics.TemporalValidityInstruction(temporalValidity);
}
