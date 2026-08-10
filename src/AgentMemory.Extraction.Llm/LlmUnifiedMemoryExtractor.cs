using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Extraction.Llm.Internal;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentMemory.Extraction.Llm;

internal sealed class LlmUnifiedMemoryExtractor : IUnifiedMemoryExtractor
{
    private const string SystemPrompt =
        """
        You extract structured long-term memory from a conversation.
        Return JSON only with all four arrays: entities, facts, preferences, relations.
        Use exactly this shape:
        {"entities":[{"name":"...","type":"PERSON|ORGANIZATION|LOCATION|EVENT|OBJECT","confidence":0.9,"aliases":[]}],"facts":[{"subject":"...","predicate":"...","object":"...","confidence":0.9}],"preferences":[{"category":"...","preference":"...","confidence":0.85}],"relations":[{"source":"...","target":"...","relation_type":"...","confidence":0.8}]}
        Use empty arrays when a category has no supported memory. Do not emit prose or markdown.
        """;

    private readonly LlmExtractionOptions _options;
    private readonly LlmExtractionRunner _runner;

    public LlmUnifiedMemoryExtractor(
        IChatClient chatClient,
        IOptions<LlmExtractionOptions> options,
        ILogger<LlmUnifiedMemoryExtractor> logger)
    {
        _options = options.Value;
        _runner = new LlmExtractionRunner(chatClient, _options, logger);
    }

    public bool IsEnabled => _options.UseUnifiedExtraction;

    public async Task<UnifiedExtractionResult> ExtractAsync(
        IReadOnlyList<Message> messages,
        CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0)
            return new UnifiedExtractionResult();

        using var activity = AgentMemoryDiagnostics.Source.StartActivity("memory.extract.unified");
        var results = await _runner.RunAsync(
            BuildSystemPrompt(_options.AssistantContent),
            "Extract all supported memory from this conversation:",
            ConversationTextBuilder.Build(messages),
            response => new[] { Project(response) },
            cancellationToken,
            failOnParseExhaustion: true).ConfigureAwait(false);
        return results.Count == 1 ? results[0] : new UnifiedExtractionResult();
    }

    private static UnifiedExtractionResult Project(LlmExtractionResponse response) =>
        new()
        {
            Entities = (response.Entities ?? [])
                .Where(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.Type))
                .Select(item => new ExtractedEntity
                {
                    Name = item.Name,
                    Type = NormalizeType(item.Type),
                    Subtype = item.Subtype,
                    Description = item.Description,
                    Confidence = item.Confidence,
                    Aliases = item.Aliases,
                }).ToArray(),
            Facts = (response.Facts ?? [])
                .Where(item => !string.IsNullOrWhiteSpace(item.Subject) &&
                               !string.IsNullOrWhiteSpace(item.Predicate) &&
                               !string.IsNullOrWhiteSpace(item.Object))
                .Select(item => new ExtractedFact
                {
                    Subject = item.Subject,
                    Predicate = item.Predicate,
                    Object = item.Object,
                    Confidence = item.Confidence,
                }).ToArray(),
            Preferences = (response.Preferences ?? [])
                .Where(item => !string.IsNullOrWhiteSpace(item.Preference))
                .Select(item => new ExtractedPreference
                {
                    Category = item.Category,
                    PreferenceText = item.Preference,
                    Context = item.Context,
                    Confidence = item.Confidence,
                }).ToArray(),
            Relationships = (response.Relations ?? [])
                .Where(item => !string.IsNullOrWhiteSpace(item.Source) &&
                               !string.IsNullOrWhiteSpace(item.Target) &&
                               !string.IsNullOrWhiteSpace(item.RelationType))
                .Select(item => new ExtractedRelationship
                {
                    SourceEntity = item.Source,
                    TargetEntity = item.Target,
                    RelationshipType = item.RelationType,
                    Description = item.Description,
                    Confidence = item.Confidence,
                }).ToArray(),
        };

    private static string NormalizeType(string type) => type.ToUpperInvariant() switch
    {
        "CONCEPT" => "OBJECT",
        "PLACE" => "LOCATION",
        "COMPANY" => "ORGANIZATION",
        "INDIVIDUAL" => "PERSON",
        var value => value,
    };

    /// <summary>The system prompt plus whatever the assistant-content setting asks for.</summary>
    internal static string BuildSystemPrompt(AssistantContentMode assistantContent) =>
        SystemPrompt + ExtractionPromptSemantics.AssistantContentInstruction(assistantContent);
}
