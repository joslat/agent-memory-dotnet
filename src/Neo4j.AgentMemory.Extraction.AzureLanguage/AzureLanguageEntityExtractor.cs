using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Core.Extraction;
using Neo4j.AgentMemory.Extraction.AzureLanguage.Internal;

namespace Neo4j.AgentMemory.Extraction.AzureLanguage;

/// <summary>
/// Extracts named entities from conversation messages using Azure AI Text Analytics.
/// </summary>
public sealed class AzureLanguageEntityExtractor : ExtractorBase<ExtractedEntity>, IEntityExtractor
{
    private readonly ITextAnalyticsClientWrapper _client;
    private readonly AzureLanguageOptions _options;

    internal AzureLanguageEntityExtractor(
        ITextAnalyticsClientWrapper client,
        IOptions<AzureLanguageOptions> options,
        ILogger<AzureLanguageEntityExtractor> logger)
        : base(logger)
    {
        _client = client;
        _options = options.Value;
    }

    protected override async Task<IReadOnlyList<ExtractedEntity>> ExtractCoreAsync(
        IReadOnlyList<Message> messages, CancellationToken ct)
    {
        var allEntities = new List<AzureRecognizedEntity>();

        foreach (var batch in messages.Chunk(_options.MaxDocumentBatchSize))
        {
            foreach (var message in batch)
            {
                if (string.IsNullOrWhiteSpace(message.Content))
                    continue;

                var entities = await _client.RecognizeEntitiesAsync(
                    message.Content, _options.DefaultLanguage, ct);
                allEntities.AddRange(entities);
            }
        }

        return allEntities
            .GroupBy(e => e.Text, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(e => e.ConfidenceScore).First())
            .Select(e => new ExtractedEntity
            {
                Name = e.Text,
                Type = MapCategory(e.Category),
                Subtype = string.IsNullOrWhiteSpace(e.SubCategory) ? null : e.SubCategory,
                Confidence = e.ConfidenceScore,
                Attributes = string.IsNullOrWhiteSpace(e.SubCategory)
                    ? new Dictionary<string, object>()
                    : new Dictionary<string, object> { ["azureSubCategory"] = e.SubCategory! }
            })
            .ToList();
    }

    internal static string MapCategory(string category) => category switch
    {
        "Person" => "PERSON",
        "Organization" => "ORGANIZATION",
        "Location" or "Address" or "GPE" => "LOCATION",
        "Event" => "EVENT",
        "Product" or "Skill" or "IP Address" or "URL" or "Email" or "Phone Number" => "OBJECT",
        "DateTime" or "Quantity" or "PersonType" => "OBJECT",
        _ => "OBJECT"
    };
}
