using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Reading;

/// <summary>An exact identity predicate on a declared outgoing relation; never a name lookup or query fragment.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleLinkFilter
{
    /// <summary>Declared relation whose source is the queried type.</summary>
    public required string Relation { get; init; }
    /// <summary>Target identity, resolved under the same owner, shared visibility and clock as the record's links.</summary>
    public required string TargetId { get; init; }
}
