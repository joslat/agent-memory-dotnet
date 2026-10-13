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
    /// <summary>Conjunctive filter clauses.</summary>
    public IReadOnlyList<ModuleViewFilter> Where { get; init; } = [];
    /// <summary>Optional property ordering.</summary>
    public ModuleViewOrder? OrderBy { get; init; }
    /// <summary>Maximum records requested by this view; host budgets can reduce it.</summary>
    public required int Limit { get; init; }
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
