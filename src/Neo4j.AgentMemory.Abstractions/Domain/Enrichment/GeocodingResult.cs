namespace Neo4j.AgentMemory.Abstractions.Domain.Enrichment;

/// <summary>
/// Result of geocoding a free-text location string.
/// </summary>
public sealed record GeocodingResult
{
    /// <summary>WGS-84 latitude in decimal degrees.</summary>
    public required double Latitude { get; init; }
    /// <summary>WGS-84 longitude in decimal degrees.</summary>
    public required double Longitude { get; init; }
    /// <summary>Human-readable formatted address returned by the provider.</summary>
    public string? FormattedAddress { get; init; }
    /// <summary>Country name.</summary>
    public string? Country { get; init; }
    /// <summary>Region, state or province.</summary>
    public string? Region { get; init; }
    /// <summary>City, town or village.</summary>
    public string? City { get; init; }
    /// <summary>Provider-specific confidence score (0–1), if available.</summary>
    public double? Confidence { get; init; }
    /// <summary>Name of the geocoding provider that produced this result.</summary>
    public string? Provider { get; init; }
}
