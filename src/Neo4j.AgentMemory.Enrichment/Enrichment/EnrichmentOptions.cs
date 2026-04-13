namespace Neo4j.AgentMemory.Enrichment;

/// <summary>
/// Configuration options for the Wikimedia enrichment service.
/// </summary>
public sealed class EnrichmentOptions
{
    public string WikipediaLanguage { get; set; } = "en";
    public string WikipediaBaseUrl { get; set; } = "https://{lang}.wikipedia.org/api/rest_v1";
    public int TimeoutSeconds { get; set; } = 10;
    public int MaxRetries { get; set; } = 2;
}
