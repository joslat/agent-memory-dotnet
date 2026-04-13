using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain.Enrichment;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Enrichment;

/// <summary>
/// Enrichment service backed by the Wikipedia REST API (Wikimedia).
/// </summary>
public sealed class WikimediaEnrichmentService : IEnrichmentService
{
    internal const string ClientName = "Wikipedia";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly EnrichmentOptions _options;
    private readonly ILogger<WikimediaEnrichmentService> _logger;

    public WikimediaEnrichmentService(
        IHttpClientFactory httpClientFactory,
        IOptions<EnrichmentOptions> options,
        ILogger<WikimediaEnrichmentService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<EnrichmentResult?> EnrichEntityAsync(
        string entityName,
        string entityType,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entityName))
            return null;

        try
        {
            var client = _httpClientFactory.CreateClient(ClientName);
            var title = Uri.EscapeDataString(entityName.Replace(' ', '_'));
            var lang = _options.WikipediaLanguage;
            var url = $"https://{lang}.wikipedia.org/api/rest_v1/page/summary/{title}";

            using var response = await client.GetAsync(url, ct).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogDebug("Wikipedia page not found for entity '{EntityName}'", entityName);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Wikipedia API returned {StatusCode} for entity '{EntityName}'",
                    (int)response.StatusCode, entityName);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var summary = JsonSerializer.Deserialize<WikipediaSummaryResponse>(json, JsonOptions);

            if (summary is null)
                return null;

            return new EnrichmentResult
            {
                EntityName = entityName,
                Summary = summary.Extract,
                Description = summary.Description,
                WikipediaUrl = summary.ContentUrls?.Desktop?.Page,
                ImageUrl = summary.Thumbnail?.Source,
                Provider = "Wikipedia",
                RetrievedAtUtc = DateTimeOffset.UtcNow
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Enrichment failed for entity '{EntityName}'", entityName);
            return null;
        }
    }

    // ---- Internal DTOs ----

    private sealed class WikipediaSummaryResponse
    {
        [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
        [JsonPropertyName("extract")] public string? Extract { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("content_urls")] public WikipediaContentUrls? ContentUrls { get; set; }
        [JsonPropertyName("thumbnail")] public WikipediaThumbnail? Thumbnail { get; set; }
    }

    private sealed class WikipediaContentUrls
    {
        [JsonPropertyName("desktop")] public WikipediaUrlSet? Desktop { get; set; }
    }

    private sealed class WikipediaUrlSet
    {
        [JsonPropertyName("page")] public string? Page { get; set; }
    }

    private sealed class WikipediaThumbnail
    {
        [JsonPropertyName("source")] public string? Source { get; set; }
    }
}
