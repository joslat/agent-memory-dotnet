namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// The kind of memory item an <see cref="IngestionItemOutcome"/> describes (#101). Deliberately
/// separate from <see cref="MemoryNodeKind"/> (which enumerates persisted graph node labels only,
/// scoped to embedding-batch regeneration): ingestion outcomes also need to describe
/// <see cref="Relationship"/> items, which are graph edges, not nodes.
/// </summary>
public enum MemoryItemKind
{
    /// <summary>An extracted/persisted entity.</summary>
    Entity,

    /// <summary>An extracted/persisted fact.</summary>
    Fact,

    /// <summary>An extracted/persisted preference.</summary>
    Preference,

    /// <summary>An extracted/persisted relationship.</summary>
    Relationship
}
