using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Extraction.AzureLanguage.Internal;

namespace Neo4j.AgentMemory.Extraction.AzureLanguage;

/// <summary>
/// Extracts facts from conversation messages using Azure AI Text Analytics
/// (key phrases and linked entity recognition).
/// </summary>
public sealed class AzureLanguageFactExtractor : IFactExtractor
{
    private readonly ITextAnalyticsClientWrapper _client;
    private readonly AzureLanguageOptions _options;
    private readonly ILogger<AzureLanguageFactExtractor> _logger;

    internal AzureLanguageFactExtractor(
        ITextAnalyticsClientWrapper client,
        IOptions<AzureLanguageOptions> options,
        ILogger<AzureLanguageFactExtractor> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ExtractedFact>> ExtractAsync(
        IReadOnlyList<Message> messages,
        CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0)
            return Array.Empty<ExtractedFact>();

        try
        {
            var facts = new List<ExtractedFact>();

            foreach (var message in messages)
            {
                if (string.IsNullOrWhiteSpace(message.Content))
                    continue;

                await AddKeyPhraseFacts(message, facts, cancellationToken);
                await AddLinkedEntityFacts(message, facts, cancellationToken);
            }

            return facts;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure Language fact extraction failed; returning empty list.");
            return Array.Empty<ExtractedFact>();
        }
    }

    private async Task AddKeyPhraseFacts(Message message, List<ExtractedFact> facts, CancellationToken ct)
    {
        var keyPhrases = await _client.ExtractKeyPhrasesAsync(
            message.Content, _options.DefaultLanguage, ct);

        var context = message.Content.Length > 100
            ? message.Content[..100] + "..."
            : message.Content;

        foreach (var phrase in keyPhrases)
        {
            if (!string.IsNullOrWhiteSpace(phrase))
            {
                facts.Add(new ExtractedFact
                {
                    Subject = phrase,
                    Predicate = "mentioned in conversation",
                    Object = context,
                    Confidence = 0.7
                });
            }
        }
    }

    private async Task AddLinkedEntityFacts(Message message, List<ExtractedFact> facts, CancellationToken ct)
    {
        var linkedEntities = await _client.RecognizeLinkedEntitiesAsync(
            message.Content, _options.DefaultLanguage, ct);

        foreach (var entity in linkedEntities)
        {
            facts.Add(new ExtractedFact
            {
                Subject = entity.Name,
                Predicate = "is described as",
                Object = entity.Url ?? entity.Name,
                Confidence = 0.8
            });
        }
    }
}
