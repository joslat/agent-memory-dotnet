using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Core.Extraction;
using Neo4j.AgentMemory.Extraction.AzureLanguage.Internal;

namespace Neo4j.AgentMemory.Extraction.AzureLanguage;

/// <summary>
/// Extracts relationships between co-occurring entities in conversation messages
/// using Azure AI Text Analytics.
/// </summary>
public sealed class AzureLanguageRelationshipExtractor : ExtractorBase<ExtractedRelationship>, IRelationshipExtractor
{
    private readonly ITextAnalyticsClientWrapper _client;
    private readonly AzureLanguageOptions _options;
    private readonly AzureExtractionContext _context;

    internal AzureLanguageRelationshipExtractor(
        ITextAnalyticsClientWrapper client,
        IOptions<AzureLanguageOptions> options,
        ILogger<AzureLanguageRelationshipExtractor> logger,
        AzureExtractionContext context)
        : base(logger)
    {
        _client = client;
        _options = options.Value;
        _context = context;
    }

    protected override async Task<IReadOnlyList<ExtractedRelationship>> ExtractCoreAsync(
        IReadOnlyList<Message> messages, CancellationToken ct)
    {
        var relationships = new List<ExtractedRelationship>();

        foreach (var message in messages)
        {
            if (string.IsNullOrWhiteSpace(message.Content))
                continue;

            var entityList = await _context.GetOrRecognizeEntitiesAsync(
                message.Content, _options.DefaultLanguage, _client, ct);

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
}
