using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Extraction.Llm.Internal;

namespace AgentMemory.Extraction.Llm;

/// <summary>
/// Extracts named entities from conversation messages using an LLM.
/// </summary>
public sealed class LlmEntityExtractor : ExtractorBase<ExtractedEntity>, IEntityExtractor
{
    public const string DefaultSystemPrompt =
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

    private readonly LlmExtractionOptions _options;
    private readonly LlmExtractionRunner _runner;

    public LlmEntityExtractor(
        IChatClient chatClient,
        IOptions<LlmExtractionOptions> options,
        ILogger<LlmEntityExtractor> logger)
        : base(logger)
    {
        _options = options.Value;
        _runner = new LlmExtractionRunner(chatClient, _options, logger);
    }

    protected override async Task<IReadOnlyList<ExtractedEntity>> ExtractCoreAsync(
        IReadOnlyList<Message> messages, CancellationToken ct)
    {
        var conversationText = ConversationTextBuilder.Build(messages);
        return await _runner.RunAsync(
            _options.EntityExtractionPrompt ?? DefaultSystemPrompt,
            "Extract entities from this conversation:",
            conversationText,
            ProjectEntities,
            ct);
    }

    private static IReadOnlyList<ExtractedEntity> ProjectEntities(LlmExtractionResponse dto)
    {
        if (dto.Entities is null) return Array.Empty<ExtractedEntity>();

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

    private static string NormalizeEntityType(string type) => type.ToUpperInvariant() switch
    {
        "CONCEPT" => "OBJECT",
        "PLACE"   => "LOCATION",
        "COMPANY" => "ORGANIZATION",
        "INDIVIDUAL" => "PERSON",
        var t => t
    };
}
