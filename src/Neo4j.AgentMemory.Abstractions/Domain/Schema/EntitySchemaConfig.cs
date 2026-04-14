#pragma warning disable CS1591
namespace Neo4j.AgentMemory.Abstractions.Domain.Schema;

/// <summary>
/// Configuration for the knowledge graph entity schema, including entity types,
/// relation types, and validation behaviour. Mirrors Python EntitySchemaConfig.
/// </summary>
public sealed record EntitySchemaConfig
{
    public string Name { get; init; } = "poleo";
    public string Version { get; init; } = "1.0";
    public string? Description { get; init; } = "POLE+O entity schema for knowledge graphs";

    public IReadOnlyList<EntityTypeConfig> EntityTypes { get; init; } =
        DefaultSchemas.GetPoleoEntityTypes();

    public IReadOnlyList<RelationTypeConfig> RelationTypes { get; init; } =
        DefaultSchemas.GetPoleoRelationTypes();

    public string DefaultEntityType { get; init; } = "OBJECT";
    public bool EnableSubtypes { get; init; } = true;
    public bool StrictTypes { get; init; } = false;

    /// <summary>Returns the names of all configured entity types.</summary>
    public IReadOnlyList<string> GetEntityTypeNames() =>
        EntityTypes.Select(et => et.Name).ToList();

    /// <summary>Returns valid subtypes for the given entity type, or empty if unknown.</summary>
    public IReadOnlyList<string> GetSubtypes(string entityType)
    {
        foreach (var et in EntityTypes)
        {
            if (string.Equals(et.Name, entityType, StringComparison.OrdinalIgnoreCase))
                return et.Subtypes;
        }
        return Array.Empty<string>();
    }

    /// <summary>
    /// Returns true if the entity type is valid. When <see cref="StrictTypes"/> is false,
    /// all types are considered valid.
    /// </summary>
    public bool IsValidType(string entityType)
    {
        if (!StrictTypes) return true;
        return EntityTypes.Any(et =>
            string.Equals(et.Name, entityType, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Normalizes an entity type to its canonical uppercase form.
    /// In strict mode, unknown types fall back to <see cref="DefaultEntityType"/>.
    /// In non-strict mode, unknown types are returned uppercased.
    /// </summary>
    public string NormalizeType(string entityType)
    {
        var typeUpper = entityType.ToUpperInvariant();
        foreach (var et in EntityTypes)
        {
            if (string.Equals(et.Name, typeUpper, StringComparison.OrdinalIgnoreCase))
                return et.Name;
        }
        return StrictTypes ? DefaultEntityType : typeUpper;
    }

    /// <summary>Returns the names of all configured relation types.</summary>
    public IReadOnlyList<string> GetRelationTypeNames() =>
        RelationTypes.Select(rt => rt.Name).ToList();
}
