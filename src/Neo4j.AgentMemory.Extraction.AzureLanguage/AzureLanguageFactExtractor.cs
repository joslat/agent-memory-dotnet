using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Core.Extraction;
using Neo4j.AgentMemory.Extraction.AzureLanguage.Internal;

namespace Neo4j.AgentMemory.Extraction.AzureLanguage;

/// <summary>
/// Extracts facts from conversation messages using Azure AI Text Analytics
/// (key phrases and linked entity recognition).
/// </summary>
public sealed class AzureLanguageFactExtractor : ExtractorBase<ExtractedFact>, IFactExtractor
{
    private readonly ITextAnalyticsClientWrapper _client;
    private readonly AzureLanguageOptions _options;

    internal AzureLanguageFactExtractor(
        ITextAnalyticsClientWrapper client,
        IOptions<AzureLanguageOptions> options,
        ILogger<AzureLanguageFactExtractor> logger)
        : base(logger)
    {
        _client = client;
        _options = options.Value;
    }

    protected override async Task<IReadOnlyList<ExtractedFact>> ExtractCoreAsync(
        IReadOnlyList<Message> messages, CancellationToken ct)
    {
        var facts = new List<ExtractedFact>();

        foreach (var message in messages)
        {
            if (string.IsNullOrWhiteSpace(message.Content))
                continue;

            await AddKeyPhraseFacts(message, facts, ct);
            await AddLinkedEntityFacts(message, facts, ct);
        }

        return facts;
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
