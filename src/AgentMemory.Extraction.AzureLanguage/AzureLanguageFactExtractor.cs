using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Extraction.AzureLanguage.Internal;

namespace AgentMemory.Extraction.AzureLanguage;

/// <summary>
/// Extracts facts from conversation messages using Azure AI Text Analytics
/// (key phrases and linked entity recognition).
/// </summary>
internal sealed class AzureLanguageFactExtractor : ExtractorBase<ExtractedFact>, IFactExtractor
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
        IReadOnlyList<Message> messages, CancellationToken cancellationToken)
    {
        var facts = new List<ExtractedFact>();

        foreach (var message in messages)
        {
            if (string.IsNullOrWhiteSpace(message.Content))
                continue;

            await AddKeyPhraseFacts(message, facts, cancellationToken).ConfigureAwait(false);
            await AddLinkedEntityFacts(message, facts, cancellationToken).ConfigureAwait(false);
        }

        return facts;
    }

    private async Task AddKeyPhraseFacts(Message message, List<ExtractedFact> facts, CancellationToken cancellationToken)
    {
        var keyPhrases = await _client.ExtractKeyPhrasesAsync(
            message.Content, _options.DefaultLanguage, cancellationToken).ConfigureAwait(false);

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
                    Confidence = _options.KeyPhraseFactConfidence
                });
            }
        }
    }

    private async Task AddLinkedEntityFacts(Message message, List<ExtractedFact> facts, CancellationToken cancellationToken)
    {
        var linkedEntities = await _client.RecognizeLinkedEntitiesAsync(
            message.Content, _options.DefaultLanguage, cancellationToken).ConfigureAwait(false);

        foreach (var entity in linkedEntities)
        {
            facts.Add(new ExtractedFact
            {
                Subject = entity.Name,
                Predicate = "is described as",
                Object = entity.Url ?? entity.Name,
                Confidence = _options.LinkedEntityFactConfidence
            });
        }
    }
}
