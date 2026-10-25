using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Reading;

/// <summary>Additional restrictions for a declared view; cannot change its module, type, predicates or ordering.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleViewArguments
{
    /// <summary>Optional exact logical key; null leaves the declared view unrestricted by key.</summary>
    public string? Key { get; init; }
    /// <summary>Conjunctive target predicates applied by storage before its result limit.</summary>
    public IReadOnlyList<ModuleLinkFilter> Links { get; init; } = [];
    /// <summary>Optional positive limit no greater than the declared view's limit.</summary>
    public int? Limit { get; init; }
    /// <summary>The text a search view matches (Extensibility 0.13 §K4); required by search views, refused by list views.</summary>
    public string? Search { get; init; }
}
