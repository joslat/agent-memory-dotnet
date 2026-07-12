using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain.Enrichment;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Enrichment;

/// <summary>
/// Enrichment service backed by the Wikipedia REST API (Wikimedia).
/// </summary>
internal sealed class WikimediaEnrichmentService : IEnrichmentService
{
    internal const string ClientName = "Wikipedia";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WikimediaEnrichmentOptions _options;
    private readonly ILogger<WikimediaEnrichmentService> _logger;

    public WikimediaEnrichmentService(
        IHttpClientFactory httpClientFactory,
        IOptions<WikimediaEnrichmentOptions> options,
        ILogger<WikimediaEnrichmentService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<EnrichmentResult?> EnrichEntityAsync(
        string entityName,
        string entityType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entityName))
            return null;

        try
        {
            var client = _httpClientFactory.CreateClient(ClientName);
            var title = Uri.EscapeDataString(entityName.Replace(' ', '_'));
            var lang = _options.WikipediaLanguage;
            // Honor the configured base URL (the default contains the {lang} token + /api/rest_v1 suffix),
            // so a mirror / internal caching proxy / non-default REST host actually takes effect. Building
            // from the literal previously ignored WikipediaBaseUrl entirely despite it being validated.
            var baseUrl = _options.WikipediaBaseUrl.Replace("{lang}", lang).TrimEnd('/');
            var url = $"{baseUrl}/page/summary/{title}";

            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

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

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // genuine caller cancellation
        }
        catch (OperationCanceledException ex)
        {
            // HttpClient.Timeout fired (caller's cancellationToken not cancelled). Surface a distinct timeout log; keep
            // the graceful null contract.
            _logger.LogWarning(ex, "Enrichment request for entity '{EntityName}' timed out", entityName);
            return null;
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
