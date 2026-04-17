namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Aggregate extraction statistics across all entities and messages.
/// </summary>
public sealed record ExtractionStats(int TotalEntities, int TotalMessages, double AvgEntitiesPerMessage);
