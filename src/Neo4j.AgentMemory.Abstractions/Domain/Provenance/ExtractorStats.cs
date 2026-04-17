namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Statistics for a specific extractor.
/// </summary>
public sealed record ExtractorStats(string ExtractorName, int EntityCount, double AvgConfidence, int TotalExtractions);
