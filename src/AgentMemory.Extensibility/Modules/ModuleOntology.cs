// SPDX-License-Identifier: Apache-2.0
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

    // Relations by name (names are unique in an ontology), built once per relations list (round 10, C-M3: every link scanned the
    // list, so loading a pack cost links times relations, minutes at the format's caps). Keyed on the list itself, so a copy made
    // with other relations gets its own index.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<ModuleRelation>, Dictionary<string, ModuleRelation>> Indexes = new();

    /// <summary>The relation of this name declared from <paramref name="from"/>, or null: one lookup, whatever the ontology's size.</summary>
    internal ModuleRelation? Relation(string name, string from) =>
        Indexes.GetValue(Relations, static list => list.Where(r => r is not null).GroupBy(r => r.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal))
            .TryGetValue(name, out var relation) && relation.From == from ? relation : null;
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
    // Feature 03's members are left out of the serialized declarations when absent, so a module that does not use them keeps
    // the declarations digest its signed decisions name (TrialGateTests pins it).
    /// <summary>The string property that is the record's main text (Core 15.1), when the type names one.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }
    /// <summary>The memory kind (semantic, episodic, procedural, preference, identity or working), for mapping and display only.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? MemoryKind { get; init; }
    /// <summary>How its records age (feature 07): <c>pinned</c> (never; no expiresAt) or <c>refreshes</c> (ttlDays after validFrom). Optional.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Lifecycle { get; init; }
    /// <summary>For a <c>refreshes</c> type, the days a record stays valid after its validFrom unless it states a validTo.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? TtlDays { get; init; }
    /// <summary>The built-in type this one binds (<c>note</c>, <c>chunk</c>, <c>document</c>), when the host bound it: never a pack author's.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? BuiltIn { get; init; }
    /// <summary>Declared property names and constraints.</summary>
    public IReadOnlyDictionary<string, ModuleProperty> Properties { get; init; } =
        new Dictionary<string, ModuleProperty>().AsReadOnly();

    /// <summary>
    /// The string properties a reader of this type sees: those its render template names, or all of them without a template.
    /// Traversal matches a question's words against them (CMP-4).
    /// </summary>
    public IReadOnlyList<string> TextProperties() => ModuleRenderTemplate.TextProperties(this);
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
    /// <summary>The built-in relation this one binds (<c>part_of</c>), when the host bound it.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? BuiltIn { get; init; }
    /// <summary>Whether a live target is required.</summary>
    public bool Required { get; init; }
    /// <summary>Optional maximum number of outgoing links.</summary>
    public int? Max { get; init; }
    /// <summary>Optional nonempty description of at most 200 characters.</summary>
    public string? Description { get; init; }
}
