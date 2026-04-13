namespace Neo4j.AgentMemory.Enrichment;

/// <summary>
/// Configuration options for the Nominatim geocoding service.
/// </summary>
public sealed class GeocodingOptions
{
    public string UserAgent { get; set; } = "Neo4j.AgentMemory/1.0";
    public string BaseUrl { get; set; } = "https://nominatim.openstreetmap.org";
    public int TimeoutSeconds { get; set; } = 10;
    public int MaxRetries { get; set; } = 2;
    /// <summary>Maximum requests per second. Nominatim requires max 1 req/sec.</summary>
    public int RateLimitPerSecond { get; set; } = 1;
}
