using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain.Enrichment;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Enrichment;

/// <summary>
/// Geocoding service backed by the Nominatim OpenStreetMap API.
/// </summary>
public sealed class NominatimGeocodingService : IGeocodingService
{
    internal const string ClientName = "Nominatim";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GeocodingOptions _options;
    private readonly ILogger<NominatimGeocodingService> _logger;

    public NominatimGeocodingService(
        IHttpClientFactory httpClientFactory,
        IOptions<GeocodingOptions> options,
        ILogger<NominatimGeocodingService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<GeocodingResult?> GeocodeAsync(string locationText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(locationText))
            return null;

        try
        {
            var client = _httpClientFactory.CreateClient(ClientName);
            var encodedQuery = Uri.EscapeDataString(locationText);
            var url = $"{_options.BaseUrl.TrimEnd('/')}/search?q={encodedQuery}&format=json&limit=1&addressdetails=1";

            using var response = await client.GetAsync(url, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Nominatim returned {StatusCode} for query '{Query}'",
                    (int)response.StatusCode, locationText);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var results = JsonSerializer.Deserialize<NominatimResult[]>(json, JsonOptions);

            if (results is null || results.Length == 0)
                return null;

            var first = results[0];
            var address = first.Address;

            return new GeocodingResult
            {
                Latitude = double.Parse(first.Lat, System.Globalization.CultureInfo.InvariantCulture),
                Longitude = double.Parse(first.Lon, System.Globalization.CultureInfo.InvariantCulture),
                FormattedAddress = first.DisplayName,
                Country = address?.Country,
                Region = address?.State,
                City = address?.City ?? address?.Town ?? address?.Village,
                Provider = "Nominatim"
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Geocoding failed for '{LocationText}'", locationText);
            return null;
        }
    }

    // ---- Internal DTOs ----

    private sealed class NominatimResult
    {
        [JsonPropertyName("lat")] public string Lat { get; set; } = string.Empty;
        [JsonPropertyName("lon")] public string Lon { get; set; } = string.Empty;
        [JsonPropertyName("display_name")] public string DisplayName { get; set; } = string.Empty;
        [JsonPropertyName("address")] public NominatimAddress? Address { get; set; }
    }

    private sealed class NominatimAddress
    {
        [JsonPropertyName("city")] public string? City { get; set; }
        [JsonPropertyName("town")] public string? Town { get; set; }
        [JsonPropertyName("village")] public string? Village { get; set; }
        [JsonPropertyName("state")] public string? State { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("country_code")] public string? CountryCode { get; set; }
    }
}
