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
    // Split either side of the entity-type list so the types can come from configuration. At default
    // settings the two halves rejoin into the exact string that shipped before — a strict requirement,
    // not a nicety: every sealed base and every quality number in the measurement plan was produced by
    // this prompt, and one character of drift changes what the model extracts.
    private const string SystemPromptPrefix =
        """
        You extract structured long-term memory from a conversation.
        Return JSON only with all four arrays: entities, facts, preferences, relations.
        Use exactly this shape:
        {"entities":[{"name":"...","type":"
        """;

    private const string SystemPromptSuffix =
        """
        ","confidence":0.9,"aliases":[]}],"facts":[{"subject":"...","predicate":"...","object":"...","confidence":0.9}],"preferences":[{"category":"...","preference":"...","confidence":0.85}],"relations":[{"source":"...","target":"...","relation_type":"...","confidence":0.8}]}
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
            BuildSystemPrompt(
                _options.AssistantContent, _options.EntityTypes, _options.TemporalValidity),
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
                    ValidFrom = item.ValidFrom,
                    ValidUntil = item.ValidUntil,
                    SourceRole = item.SourceRole,
                }).ToArray(),
            Preferences = (response.Preferences ?? [])
                .Where(item => !string.IsNullOrWhiteSpace(item.Preference))
                .Select(item => new ExtractedPreference
                {
                    Category = item.Category,
                    PreferenceText = item.Preference,
                    Context = item.Context,
                    Confidence = item.Confidence,
                    SourceRole = item.SourceRole,
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

    /// <summary>The system prompt at default entity types, plus the assistant-content instruction.</summary>
    internal static string BuildSystemPrompt(AssistantContentMode assistantContent) =>
        BuildSystemPrompt(assistantContent, LlmEntityExtractor.DefaultEntityTypes);

    /// <summary>
    /// The system prompt built over <paramref name="entityTypes"/>, plus whatever the
    /// assistant-content setting asks for.
    /// </summary>
    /// <remarks>
    /// The types used to be a hardcoded const, so <see cref="LlmExtractionOptions.EntityTypes"/> was
    /// silently dropped whenever unified extraction was enabled. That is the defect that blocked
    /// making unified the shipped default: flipping it would have stopped honouring a setting
    /// consumers had already configured, with no error and no compile break.
    /// <para>
    /// Falls back to the built-in POLE+O list on an empty collection, matching
    /// <see cref="LlmEntityExtractor"/> — an empty list is a misconfiguration, and emitting
    /// <c>"type":""</c> would ask the model for a type it can never satisfy.
    /// </para>
    /// </remarks>
    internal static string BuildSystemPrompt(
        AssistantContentMode assistantContent, IReadOnlyList<string> entityTypes) =>
        BuildSystemPrompt(assistantContent, entityTypes, TemporalValidityMode.Ignore);

    /// <inheritdoc cref="BuildSystemPrompt(AssistantContentMode, IReadOnlyList{string})"/>
    internal static string BuildSystemPrompt(
        AssistantContentMode assistantContent,
        IReadOnlyList<string> entityTypes,
        TemporalValidityMode temporalValidity)
    {
        var types = entityTypes is { Count: > 0 } ? entityTypes : LlmEntityExtractor.DefaultEntityTypes;
        return SystemPromptPrefix
            + string.Join('|', types)
            + SystemPromptSuffix
            + ExtractionPromptSemantics.AssistantContentInstruction(assistantContent)
            + ExtractionPromptSemantics.TemporalValidityInstruction(temporalValidity);
    }
}
