namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Abstraction for generating unique identifiers to support testability.
/// </summary>
public interface IIdGenerator
{
    /// <summary>
    /// Generates a new unique identifier.
    /// </summary>
    string GenerateId();
}
