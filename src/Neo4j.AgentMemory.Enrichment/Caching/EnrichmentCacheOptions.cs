namespace Neo4j.AgentMemory.Enrichment;

/// <summary>
/// Options that control the in-memory cache behaviour for enrichment decorators.
/// </summary>
public sealed class EnrichmentCacheOptions
{
    public TimeSpan GeocodingCacheDuration { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan EnrichmentCacheDuration { get; set; } = TimeSpan.FromHours(24);
}
