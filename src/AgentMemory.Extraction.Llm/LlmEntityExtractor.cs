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
    /// <summary>The canonical POLE+O entity types, used when no custom set is configured.</summary>
    internal static readonly IReadOnlyList<string> DefaultEntityTypes =
        new[] { "PERSON", "ORGANIZATION", "LOCATION", "EVENT", "OBJECT" };

    /// <summary>Rich descriptions for the built-in POLE+O types; custom types fall back to a generic line.</summary>
    private static readonly IReadOnlyDictionary<string, string> KnownTypeDescriptions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PERSON"] = "Individuals by name or role",
            ["ORGANIZATION"] = "Companies, groups, institutions",
            ["LOCATION"] = "Places, addresses, geographic areas",
            ["EVENT"] = "Incidents, meetings, occurrences",
            ["OBJECT"] = "Physical or digital items, concepts, technologies",
        };

    /// <summary>
    /// The default entity-extraction system prompt for the canonical POLE+O type set. Kept for
    /// back-compat; the prompt actually sent is built from the configured
    /// <see cref="LlmExtractionOptions.EntityTypes"/> via <see cref="BuildSystemPrompt"/>.
    /// </summary>
    public static readonly string DefaultSystemPrompt = BuildSystemPrompt(DefaultEntityTypes);

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
            _options.EntityExtractionPrompt ?? BuildSystemPrompt(_options.EntityTypes),
            "Extract entities from this conversation:",
            conversationText,
            ProjectEntities,
            ct);
    }

    /// <summary>
    /// Builds the entity-extraction system prompt, listing the supplied <paramref name="entityTypes"/>
    /// (falling back to <see cref="DefaultEntityTypes"/> when none are configured). Built-in POLE+O types
    /// get their canonical descriptions; any custom type is listed with a generic descriptor. Newlines are
    /// normalized to LF so the output is stable regardless of source line endings.
    /// </summary>
    internal static string BuildSystemPrompt(IReadOnlyList<string> entityTypes)
    {
        var types = entityTypes is { Count: > 0 } ? entityTypes : DefaultEntityTypes;
        var typeLines = string.Join("\n", types.Select(FormatTypeLine));

        // Assembled as header + dynamic type lines + footer (joined with LF) rather than a single
        // interpolated raw literal, because a multi-line interpolation hole inside a raw string literal
        // has ambiguous continuation-line indentation. "" injects the blank line before "Output format:".
        const string header =
            """
            You are an entity extraction assistant. Extract named entities from the conversation.
            Return JSON only — no markdown, no explanation.

            Entity Types (POLE+O Model):
            """;

        const string footer =
            """
            Output format:
            {"entities": [{"name": "...", "type": "ENTITY_TYPE", "subtype": "optional or null", "description": "brief context", "confidence": 0.9, "aliases": ["alt_name"]}]}

            Guidelines:
            - Use UPPERCASE for type
            - Confidence: 0.95 for explicit mentions, 0.8 for inferred
            - Include aliases when mentioned
            - Do not extract pronouns or generic references
            - Return {"entities": []} if nothing found
            """;

        return string.Join("\n",
            header.Replace("\r\n", "\n"),
            typeLines,
            "",
            footer.Replace("\r\n", "\n"));
    }

    private static string FormatTypeLine(string type)
    {
        var name = (type ?? string.Empty).Trim().ToUpperInvariant();
        return KnownTypeDescriptions.TryGetValue(name, out var desc)
            ? $"- {name}: {desc}"
            : $"- {name}: Domain-specific entity type";
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
