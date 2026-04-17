using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Core.Extraction;
using Neo4j.AgentMemory.Extraction.AzureLanguage.Internal;

namespace Neo4j.AgentMemory.Extraction.AzureLanguage;

/// <summary>
/// Extracts user preferences from conversation messages using Azure AI Text Analytics
/// (sentiment analysis combined with key phrase extraction).
/// </summary>
public sealed class AzureLanguagePreferenceExtractor : ExtractorBase<ExtractedPreference>, IPreferenceExtractor
{
    private readonly ITextAnalyticsClientWrapper _client;
    private readonly AzureLanguageOptions _options;

    internal AzureLanguagePreferenceExtractor(
        ITextAnalyticsClientWrapper client,
        IOptions<AzureLanguageOptions> options,
        ILogger<AzureLanguagePreferenceExtractor> logger)
        : base(logger)
    {
        _client = client;
        _options = options.Value;
    }

    protected override async Task<IReadOnlyList<ExtractedPreference>> ExtractCoreAsync(
        IReadOnlyList<Message> messages, CancellationToken ct)
    {
        var preferences = new List<ExtractedPreference>();

        foreach (var message in messages)
        {
            if (string.IsNullOrWhiteSpace(message.Content))
                continue;

            await ExtractPreferencesFromMessage(message, preferences, ct);
        }

        return preferences;
    }

    private async Task ExtractPreferencesFromMessage(
        Message message,
        List<ExtractedPreference> preferences,
        CancellationToken ct)
    {
        var sentiment = await _client.AnalyzeSentimentAsync(
            message.Content, _options.DefaultLanguage, ct);

        var keyPhrases = await _client.ExtractKeyPhrasesAsync(
            message.Content, _options.DefaultLanguage, ct);

        var stronglyPositive = sentiment.PositiveScore >= _options.PreferenceSentimentThreshold;
        var stronglyNegative = sentiment.NegativeScore >= _options.PreferenceSentimentThreshold;

        if (!stronglyPositive && !stronglyNegative)
            return;

        var context = message.Content.Length > 120
            ? message.Content[..120] + "..."
            : message.Content;

        foreach (var phrase in keyPhrases)
        {
            if (string.IsNullOrWhiteSpace(phrase))
                continue;

            if (stronglyPositive)
            {
                preferences.Add(new ExtractedPreference
                {
                    Category = "like",
                    PreferenceText = $"likes {phrase}",
                    Context = context,
                    Confidence = sentiment.PositiveScore
                });
            }
            else
            {
                preferences.Add(new ExtractedPreference
                {
                    Category = "dislike",
                    PreferenceText = $"dislikes {phrase}",
                    Context = context,
                    Confidence = sentiment.NegativeScore
                });
            }
        }
    }
}
