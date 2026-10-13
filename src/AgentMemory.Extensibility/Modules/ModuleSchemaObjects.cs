using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Modules;

/// <summary>Data-only derived schema shapes. Describing names never applies schema or creates a ledger.</summary>
[Experimental("AMEXT001")]
public static class ModuleSchemaObjects
{
    /// <summary>Describes validated local ontology shapes in ordinal name order.</summary>
    /// <remarks>The complete-set resolver must still check cross-module collisions. Migration ownership,
    /// trust, database readiness and existing database objects remain storage/activation obligations.</remarks>
    public static IReadOnlyList<ModuleSchemaObject> Describe(ModuleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var errors = manifest.Validate();
        if (errors.Count != 0) throw new FormatException(string.Join(Environment.NewLine, errors));
        return (manifest.Ontology?.Types ?? []).SelectMany(ForType)
            .OrderBy(shape => shape.Name, StringComparer.Ordinal).ToList().AsReadOnly();
    }

    internal static IEnumerable<ModuleSchemaObject> ForType(ModuleType type)
    {
        var name = type.Name ?? "";
        var stem = ModuleManifestValidator.SchemaName(name);
        yield return Shape(stem + "_id", name, "id", ModuleSchemaObjectKind.UniqueConstraint);
        yield return Shape(stem + "_owner_id_idx", name, "owner_id", ModuleSchemaObjectKind.RangeIndex);
        yield return Shape(stem + "_key_id_idx", name, "key_id", ModuleSchemaObjectKind.RangeIndex);
        if (type.Properties is null) yield break;
        foreach (var property in type.Properties.Where(pair => pair.Value?.Indexed == true).OrderBy(pair => pair.Key, StringComparer.Ordinal))
            yield return Shape(stem + "_" + ModuleManifestValidator.SchemaName(property.Key) + "_idx", name, property.Key, ModuleSchemaObjectKind.RangeIndex);
    }

    private static ModuleSchemaObject Shape(string name, string label, string property, ModuleSchemaObjectKind kind) =>
        new() { Name = name, Label = label, Property = property, Kind = kind };
}

/// <summary>A derived module schema object; it carries no executable Cypher.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleSchemaObject
{
    /// <summary>Deterministic schema-object identity.</summary>
    public required string Name { get; init; }
    /// <summary>Owned node label.</summary>
    public required string Label { get; init; }
    /// <summary>Exact property identity; normalization applies to Name only.</summary>
    public required string Property { get; init; }
    /// <summary>Requested uniqueness or range-index shape.</summary>
    public required ModuleSchemaObjectKind Kind { get; init; }
}

/// <summary>Derived schema kinds supported by the module ontology.</summary>
[Experimental("AMEXT001")]
public enum ModuleSchemaObjectKind
{
    /// <summary>Unique node property constraint.</summary>
    UniqueConstraint,
    /// <summary>Node property range index.</summary>
    RangeIndex
}
