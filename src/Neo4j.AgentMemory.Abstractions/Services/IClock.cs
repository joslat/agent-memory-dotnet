namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Abstraction for time operations to support testability.
/// </summary>
public interface IClock
{
    /// <summary>
    /// Gets the current UTC time.
    /// </summary>
    DateTimeOffset UtcNow { get; }
}
