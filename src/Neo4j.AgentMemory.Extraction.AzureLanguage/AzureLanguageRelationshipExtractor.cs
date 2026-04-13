using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Extraction.AzureLanguage.Internal;

namespace Neo4j.AgentMemory.Extraction.AzureLanguage;

/// <summary>
/// Extracts relationships between co-occurring entities in conversation messages
/// using Azure AI Text Analytics.
/// </summary>
public sealed class AzureLanguageRelationshipExtractor : IRelationshipExtractor
{
    private readonly ITextAnalyticsClientWrapper _client;
    private readonly AzureLanguageOptions _options;
    private readonly ILogger<AzureLanguageRelationshipExtractor> _logger;

    internal AzureLanguageRelationshipExtractor(
        ITextAnalyticsClientWrapper client,
        IOptions<AzureLanguageOptions> options,
        ILogger<AzureLanguageRelationshipExtractor> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ExtractedRelationship>> ExtractAsync(
        IReadOnlyList<Message> messages,
        CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0)
            return Array.Empty<ExtractedRelationship>();

        try
        {
            var relationships = new List<ExtractedRelationship>();

            foreach (var message in messages)
            {
                if (string.IsNullOrWhiteSpace(message.Content))
                    continue;

                var entities = await _client.RecognizeEntitiesAsync(
                    message.Content, _options.DefaultLanguage, cancellationToken);

                var entityList = entities.ToList();

                // Infer relationships from co-occurring entity pairs within the same message
                for (int i = 0; i < entityList.Count - 1; i++)
                {
                    for (int j = i + 1; j < entityList.Count; j++)
                    {
                        var source = entityList[i];
                        var target = entityList[j];
                        var confidence = (source.ConfidenceScore + target.ConfidenceScore) / 2.0;

                        relationships.Add(new ExtractedRelationship
                        {
                            SourceEntity = source.Text,
                            TargetEntity = target.Text,
                            RelationshipType = "co-occurs with",
                            Confidence = confidence
                        });
                    }
                }
            }

            return relationships;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure Language relationship extraction failed; returning empty list.");
            return Array.Empty<ExtractedRelationship>();
        }
    }
}
