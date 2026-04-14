namespace Neo4j.AgentMemory.Abstractions.Options;

/// <summary>
/// Configuration options for the Diffbot Knowledge Graph enrichment service.
/// </summary>
public sealed record DiffbotEnrichmentOptions
{
    /// <summary>Diffbot API key. Required.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Minimum seconds between requests. Default 0.2 (~5 req/sec).</summary>
    public double RateLimitSeconds { get; set; } = 0.2;

    /// <summary>Per-request HTTP timeout. Default 15 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Diffbot KG v3 base URL.</summary>
    public string BaseUrl { get; set; } = "https://kg.diffbot.com/kg/v3";
}
