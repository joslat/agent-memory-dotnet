#pragma warning disable CS1591
namespace Neo4j.AgentMemory.Abstractions.Domain.Schema;

/// <summary>Available schema models for knowledge graph entity classification.</summary>
public enum SchemaModel
{
    /// <summary>Person, Object, Location, Event, Organization (POLE+O) — default.</summary>
    Poleo,

    /// <summary>Original EntityType enum for backward compatibility.</summary>
    Legacy,

    /// <summary>User-defined schema loaded from JSON or composed at runtime.</summary>
    Custom
}
