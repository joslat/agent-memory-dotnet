using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Extraction.AzureLanguage.Internal;

namespace Neo4j.AgentMemory.Extraction.AzureLanguage;

/// <summary>
/// Extracts named entities from conversation messages using Azure AI Text Analytics.
/// </summary>
public sealed class AzureLanguageEntityExtractor : IEntityExtractor
{
    private readonly ITextAnalyticsClientWrapper _client;
    private readonly AzureLanguageOptions _options;
    private readonly ILogger<AzureLanguageEntityExtractor> _logger;

    internal AzureLanguageEntityExtractor(
        ITextAnalyticsClientWrapper client,
        IOptions<AzureLanguageOptions> options,
        ILogger<AzureLanguageEntityExtractor> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ExtractedEntity>> ExtractAsync(
        IReadOnlyList<Message> messages,
        CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0)
            return Array.Empty<ExtractedEntity>();

        try
        {
            var allEntities = new List<AzureRecognizedEntity>();

            foreach (var batch in messages.Chunk(_options.MaxDocumentBatchSize))
            {
                foreach (var message in batch)
                {
                    if (string.IsNullOrWhiteSpace(message.Content))
                        continue;

                    var entities = await _client.RecognizeEntitiesAsync(
                        message.Content, _options.DefaultLanguage, cancellationToken);
                    allEntities.AddRange(entities);
                }
            }

            // Deduplicate by name (case-insensitive), keeping highest confidence
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure Language entity extraction failed; returning empty list.");
            return Array.Empty<ExtractedEntity>();
        }
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
