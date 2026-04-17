namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Represents a pair of entities flagged as potential duplicates via a SAME_AS relationship.
/// </summary>
public sealed record DuplicatePair(Entity Source, Entity Target, double Similarity, string Status);
