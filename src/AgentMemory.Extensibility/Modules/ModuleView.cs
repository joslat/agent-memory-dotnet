using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace AgentMemory.Extensibility.Modules;

/// <summary>A bounded declarative read over an owned type.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleView
{
    /// <summary>The local view identifier; ownership is supplied by its manifest.</summary>
    public required string Id { get; init; }
    /// <summary>The owned type being queried.</summary>
    public required string Type { get; init; }
    /// <summary>Snapshot by default. History requires an exact key and uses fixed newest-version-first order.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public ModuleRecordSelection Selection { get; init; }
    /// <summary>Conjunctive filter clauses.</summary>
    public IReadOnlyList<ModuleViewFilter> Where { get; init; } = [];
    /// <summary>Optional property ordering.</summary>
    public ModuleViewOrder? OrderBy { get; init; }
    /// <summary>Maximum records requested by this view; host budgets can reduce it.</summary>
    public required int Limit { get; init; }
    /// <summary>
    /// A full-text search over declared string properties (Extensibility 0.13 §K4): the view returns the best matches for a
    /// request's search text, ordered by score. Null for a list view.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ModuleViewSearch? Search { get; init; }
    /// <summary>What the view answers, for the model choosing one (feature 06): display text of at most 160 characters. Optional.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; init; }
}

/// <summary>
/// How a host shows a module or pack (Extensibility 0.13 §D): DemoBrain's memory chips use it. Shown to people only; it never
/// reaches a model's context.
/// </summary>
[Experimental("AMEXT001")]
public sealed record ModuleDisplay
{
    /// <summary>A short name, 1 to 60 characters.</summary>
    public string? Title { get; init; }
    /// <summary>One or two sentences, at most 300 characters.</summary>
    public string? Description { get; init; }
    /// <summary>A knowledge pack's icon: a PNG in its signed bundle, at most 64 KiB and 512 pixels a side.</summary>
    public string? Icon { get; init; }
    /// <summary>The colour that stands for it, <c>#RRGGBB</c>; a host may let its user override it.</summary>
    public string? Color { get; init; }
}

/// <summary>The properties a search view matches; the migrator derives one full-text index over them.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleViewSearch
{
    /// <summary>At most this many records a search view returns.</summary>
    public const int MaximumLimit = 24;
    /// <summary>Declared string properties, in index order.</summary>
    public required IReadOnlyList<string> Properties { get; init; }
}

/// <summary>One closed-language predicate; values are data, never executable query fragments.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleViewFilter
{
    /// <summary>Declared property name.</summary>
    public required string Property { get; init; }
    /// <summary>One of =, !=, &lt;, &lt;=, &gt;, &gt;= and in.</summary>
    public required string Operator { get; init; }
    /// <summary>A literal, a literal list, or a supported placeholder; cloned from parser storage.</summary>
    public required JsonElement Value { get; init; }
}

/// <summary>Ordering on a declared property.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleViewOrder
{
    /// <summary>The declared property to order by.</summary>
    public required string Property { get; init; }
    /// <summary>False for ascending, including the short string notation.</summary>
    public bool Descending { get; init; }
}
