namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Types of memory that can be extracted.
/// </summary>
[Flags]
public enum ExtractionTypes
{
    /// <summary>
    /// No extraction.
    /// </summary>
    None = 0,

    /// <summary>
    /// Extract entities.
    /// </summary>
    Entities = 1 << 0,

    /// <summary>
    /// Extract relationships.
    /// </summary>
    Relationships = 1 << 1,

    /// <summary>
    /// Extract facts.
    /// </summary>
    Facts = 1 << 2,

    /// <summary>
    /// Extract preferences.
    /// </summary>
    Preferences = 1 << 3,

    /// <summary>
    /// Extract all types.
    /// </summary>
    All = Entities | Relationships | Facts | Preferences
}
