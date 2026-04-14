using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Neo4j.AgentMemory.Abstractions.Domain.Enrichment;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Enrichment;

/// <summary>
/// Enrichment service backed by the Diffbot Knowledge Graph API.
/// Supports PERSON, ORGANIZATION, LOCATION, OBJECT, and EVENT entity types.
/// </summary>
public sealed class DiffbotEnrichmentService : IEnrichmentService
{
    internal const string ClientName = "Diffbot";

    private static readonly IReadOnlyDictionary<string, string> TypeMapping =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PERSON"] = "Person",
            ["ORGANIZATION"] = "Organization",
            ["LOCATION"] = "Place",
            ["OBJECT"] = "Product",
            ["EVENT"] = "Event",
        };

    private static readonly IReadOnlySet<string> SupportedTypes =
        new HashSet<string>(TypeMapping.Keys, StringComparer.OrdinalIgnoreCase);

    private static readonly string[] RelationFields =
    [
        "employers", "subsidiaries", "founders", "locations",
        "parent", "children", "spouses", "affiliations"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly DiffbotEnrichmentOptions _options;
    private readonly ILogger<DiffbotEnrichmentService> _logger;

    // Rate-limit state
    private readonly SemaphoreSlim _rateLock = new(1, 1);
    private DateTimeOffset _lastRequestTime = DateTimeOffset.MinValue;

    public DiffbotEnrichmentService(
        HttpClient httpClient,
        DiffbotEnrichmentOptions options,
        ILogger<DiffbotEnrichmentService> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    public async Task<EnrichmentResult?> EnrichEntityAsync(
        string entityName,
        string entityType,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entityName))
            return null;

        // Unsupported entity type → return Skipped result immediately
        if (!SupportedTypes.Contains(entityType))
        {
            _logger.LogDebug("Diffbot: unsupported entity type '{EntityType}' for '{EntityName}'",
                entityType, entityName);
            return new EnrichmentResult
            {
                EntityName = entityName,
                EntityType = entityType,
                Provider = "diffbot",
                Status = EnrichmentStatus.Skipped,
                ErrorMessage = $"Entity type {entityType} not supported",
                RetrievedAtUtc = DateTimeOffset.UtcNow,
            };
        }

        await ApplyRateLimitAsync(ct).ConfigureAwait(false);

        try
        {
            var diffbotType = TypeMapping[entityType];
            var query = $"name:\"{entityName}\" type:{diffbotType}";
            var url = $"{_options.BaseUrl}/dql?type=query&token={Uri.EscapeDataString(_options.ApiKey)}&query={Uri.EscapeDataString(query)}&size=1";

            using var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning("Diffbot: invalid API key");
                return new EnrichmentResult
                {
                    EntityName = entityName,
                    EntityType = entityType,
                    Provider = "diffbot",
                    Status = EnrichmentStatus.Error,
                    ErrorMessage = "Invalid Diffbot API key",
                    RetrievedAtUtc = DateTimeOffset.UtcNow,
                };
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning("Diffbot: rate limited for entity '{EntityName}'", entityName);
                return new EnrichmentResult
                {
                    EntityName = entityName,
                    EntityType = entityType,
                    Provider = "diffbot",
                    Status = EnrichmentStatus.RateLimited,
                    ErrorMessage = "Rate limited by Diffbot API",
                    RetrievedAtUtc = DateTimeOffset.UtcNow,
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Diffbot: HTTP {StatusCode} for entity '{EntityName}'",
                    (int)response.StatusCode, entityName);
                return new EnrichmentResult
                {
                    EntityName = entityName,
                    EntityType = entityType,
                    Provider = "diffbot",
                    Status = EnrichmentStatus.Error,
                    ErrorMessage = $"HTTP error {(int)response.StatusCode}",
                    RetrievedAtUtc = DateTimeOffset.UtcNow,
                };
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var data = JsonNode.Parse(json);

            return ParseResponse(data, entityName, entityType);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Diffbot enrichment failed for entity '{EntityName}'", entityName);
            return new EnrichmentResult
            {
                EntityName = entityName,
                EntityType = entityType,
                Provider = "diffbot",
                Status = EnrichmentStatus.Error,
                ErrorMessage = ex.Message,
                RetrievedAtUtc = DateTimeOffset.UtcNow,
            };
        }
    }

    // ---- Private helpers ----

    private async Task ApplyRateLimitAsync(CancellationToken ct)
    {
        await _rateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var elapsed = (DateTimeOffset.UtcNow - _lastRequestTime).TotalSeconds;
            if (elapsed < _options.RateLimitSeconds)
                await Task.Delay(
                    TimeSpan.FromSeconds(_options.RateLimitSeconds - elapsed),
                    ct).ConfigureAwait(false);

            _lastRequestTime = DateTimeOffset.UtcNow;
        }
        finally
        {
            _rateLock.Release();
        }
    }

    private static EnrichmentResult ParseResponse(
        JsonNode? data,
        string entityName,
        string entityType)
    {
        var entities = data?["data"]?.AsArray();

        if (entities is null || entities.Count == 0)
        {
            return new EnrichmentResult
            {
                EntityName = entityName,
                EntityType = entityType,
                Provider = "diffbot",
                Status = EnrichmentStatus.NotFound,
                RetrievedAtUtc = DateTimeOffset.UtcNow,
            };
        }

        var entity = entities[0]!;

        var description = entity["description"]?.GetValue<string>()
                       ?? entity["summary"]?.GetValue<string>();
        var summary = entity["summary"]?.GetValue<string>();
        var diffbotUri = entity["diffbotUri"]?.GetValue<string>();
        var sourceUrl = entity["origin"]?.GetValue<string>()
                     ?? entity["homepageUri"]?.GetValue<string>();

        // Images
        var images = new List<string>();
        var primaryImage = entity["image"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(primaryImage))
            images.Add(primaryImage);

        var imageArray = entity["images"]?.AsArray();
        if (imageArray is not null)
        {
            foreach (var img in imageArray)
            {
                var imgUrl = img?.GetValue<string>();
                if (!string.IsNullOrEmpty(imgUrl))
                    images.Add(imgUrl);
            }
        }

        // Related entities
        var related = new List<RelatedEntity>();
        foreach (var relType in RelationFields)
        {
            var relNode = entity[relType];
            if (relNode is null) continue;

            // Handle both array and single-object values
            var items = relNode is JsonArray arr ? arr : new JsonArray(relNode.DeepClone());
            foreach (var item in items)
            {
                if (item is not JsonObject obj) continue;
                var name = obj["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name)) continue;
                related.Add(new RelatedEntity
                {
                    Name = name,
                    Relation = relType,
                    DiffbotUri = obj["diffbotUri"]?.GetValue<string>(),
                });
            }
        }

        // Confidence from importance score: min(1.0, importance/100 + 0.5)
        var importance = entity["importance"]?.GetValue<double>() ?? 0.0;
        var confidence = Math.Min(1.0, importance / 100.0 + 0.5);

        // Properties: common + type-specific metadata
        var props = new Dictionary<string, string>();

        var types = entity["types"]?.AsArray();
        if (types is not null)
            props["types"] = types.ToJsonString();

        SetPropIfPresent(props, "importance", importance.ToString("G"));

        var nbEdges = entity["nbIncomingEdges"];
        if (nbEdges is not null)
            props["nbIncomingEdges"] = nbEdges.ToJsonString();

        var upperType = entityType.ToUpperInvariant();

        if (upperType == "PERSON")
        {
            SetPropIfPresent(props, "birthDate", entity["birthDate"]?.GetValue<string>());
            SetPropIfPresent(props, "deathDate", entity["deathDate"]?.GetValue<string>());
            SetPropIfPresent(props, "gender", entity["gender"]?.GetValue<string>());
            SetArrayProp(props, "nationalities", entity["nationalities"]);
            SetArrayProp(props, "educations", entity["educations"]);
            SetArrayProp(props, "employments", entity["employments"]);
        }
        else if (upperType == "ORGANIZATION")
        {
            SetPropIfPresent(props, "foundingDate", entity["foundingDate"]?.ToJsonString());
            SetPropIfPresent(props, "nbEmployees", entity["nbEmployees"]?.ToJsonString());
            SetPropIfPresent(props, "nbEmployeesMin", entity["nbEmployeesMin"]?.ToJsonString());
            SetPropIfPresent(props, "nbEmployeesMax", entity["nbEmployeesMax"]?.ToJsonString());
            SetPropIfPresent(props, "revenue", entity["revenue"]?.ToJsonString());
            SetArrayProp(props, "industries", entity["industries"]);
            SetArrayProp(props, "categories", entity["categories"]);
            SetPropIfPresent(props, "isPublic", entity["isPublic"]?.ToJsonString());
            SetPropIfPresent(props, "stock", entity["stock"]?.ToJsonString());
        }
        else if (upperType == "LOCATION")
        {
            SetPropIfPresent(props, "country", entity["country"]?.ToJsonString());
            SetPropIfPresent(props, "region", entity["region"]?.ToJsonString());
            SetPropIfPresent(props, "city", entity["city"]?.ToJsonString());
            SetPropIfPresent(props, "latitude", entity["latitude"]?.ToJsonString());
            SetPropIfPresent(props, "longitude", entity["longitude"]?.ToJsonString());
            SetPropIfPresent(props, "population", entity["population"]?.ToJsonString());
        }

        return new EnrichmentResult
        {
            EntityName = entityName,
            EntityType = entityType,
            Provider = "diffbot",
            Status = EnrichmentStatus.Success,
            Description = description,
            Summary = summary,
            DiffbotUri = diffbotUri,
            ImageUrl = images.Count > 0 ? images[0] : null,
            Images = images,
            RelatedEntities = related,
            SourceUrl = sourceUrl,
            Confidence = confidence,
            Properties = props,
            RetrievedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private static void SetPropIfPresent(Dictionary<string, string> props, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            props[key] = value;
    }

    private static void SetArrayProp(Dictionary<string, string> props, string key, JsonNode? node)
    {
        if (node is null) return;
        var json = node.ToJsonString();
        if (json != "[]" && json != "null")
            props[key] = json;
    }
}
