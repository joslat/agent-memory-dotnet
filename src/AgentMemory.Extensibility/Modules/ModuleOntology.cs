using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace AgentMemory.Extensibility.Modules;

/// <summary>Module-owned type and relationship declarations.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleOntology
{
    /// <summary>The label namespace claimed by this ontology.</summary>
    public required string LabelPrefix { get; init; }
    /// <summary>Owned node types.</summary>
    public IReadOnlyList<ModuleType> Types { get; init; } = [];
    /// <summary>Outgoing relationships from owned node types.</summary>
    public IReadOnlyList<ModuleRelation> Relations { get; init; } = [];
}

/// <summary>One owned node type; SDK provenance and version fields are not user properties.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleType
{
    /// <summary>The namespaced Neo4j label.</summary>
    public required string Name { get; init; }
    /// <summary>Owner-scoped by default; shared writes require separate admission.</summary>
    public string Scope { get; init; } = "owner";
    /// <summary>Optional human/model-facing description.</summary>
    public string? Description { get; init; }
    /// <summary>Optional UI color.</summary>
    public string? Color { get; init; }
    /// <summary>Optional rendering template.</summary>
    public string? Render { get; init; }
    /// <summary>Declared property names and constraints.</summary>
    public IReadOnlyDictionary<string, ModuleProperty> Properties { get; init; } =
        new Dictionary<string, ModuleProperty>().AsReadOnly();
}

/// <summary>One declared property. Numeric bounds preserve their original JSON precision.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleProperty
{
    /// <summary>One of string, int, float, bool, date and datetime.</summary>
    public required string Type { get; init; }
    /// <summary>Whether admitted records must supply this property.</summary>
    public bool Required { get; init; }
    /// <summary>Whether the derived schema indexes this property.</summary>
    public bool Indexed { get; init; }
    /// <summary>Allowed strings; empty means no enumeration was declared.</summary>
    public IReadOnlyList<string> Values { get; init; } = [];
    /// <summary>Optional maximum string length.</summary>
    public int? MaxLength { get; init; }
    /// <summary>Optional inclusive numeric lower bound, independent of a disposed parser document.</summary>
    public JsonElement? Minimum { get; init; }
    /// <summary>Optional inclusive numeric upper bound, independent of a disposed parser document.</summary>
    public JsonElement? Maximum { get; init; }
    /// <summary>Optional nonempty description of at most 200 characters.</summary>
    public string? Description { get; init; }
}

/// <summary>An outgoing link declaration; it grants no permission to change the target.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleRelation
{
    /// <summary>The namespaced relationship type.</summary>
    public required string Name { get; init; }
    /// <summary>Own source type name.</summary>
    public required string From { get; init; }
    /// <summary>Own type, core:Entity, or a declared dependency's qualified type.</summary>
    public required string To { get; init; }
    /// <summary>Whether a live target is required.</summary>
    public bool Required { get; init; }
    /// <summary>Optional maximum number of outgoing links.</summary>
    public int? Max { get; init; }
    /// <summary>Optional nonempty description of at most 200 characters.</summary>
    public string? Description { get; init; }
}
