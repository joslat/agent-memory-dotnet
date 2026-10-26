using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Modules;

namespace AgentMemory.Extensibility.Reading;

/// <summary>
/// The host-level read capability traversal context walks with (coding packs CMP-1). Unlike <see cref="IModuleReader"/> it is not
/// bound to one calling module: the host grants it to the walk, which reads only what manifests declare traversable, live
/// records only, within the scope the host's isolation policy resolves.
/// </summary>
[Experimental("AMEXT001")]
public interface IModuleTraversalReader
{
    /// <summary>
    /// The live records of one type whose anchor property may match the anchors. Exact and prefix matches are filtered by the
    /// store; glob and name matches return the records whose property is set, up to the limit, for the walk to match.
    /// </summary>
    Task<ModuleReadResult> MatchAsync(ModuleAnchorQuery query, ModuleReadContext context, CancellationToken cancellationToken = default);

    /// <summary>One hop through one owned link: for each starting record, the live records it leads to, at most the fan-out each.</summary>
    Task<ModuleTraversalStep> ExpandAsync(ModuleExpandQuery query, ModuleReadContext context, CancellationToken cancellationToken = default);
}

/// <summary>The records of one type whose anchor property may match a turn's anchors.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleAnchorQuery
{
    /// <summary>Most records one anchor query returns.</summary>
    public const int MaximumLimit = 2048;

    /// <summary>The module that owns the type.</summary>
    public required string ModuleId { get; init; }
    /// <summary>The owned type.</summary>
    public required string Type { get; init; }
    /// <summary>The declared string property.</summary>
    public required string Property { get; init; }
    /// <summary>How the property's value is compared.</summary>
    public required ModuleAnchorMatch Match { get; init; }
    /// <summary>The anchor values of the matching kind.</summary>
    public required IReadOnlyList<string> Values { get; init; }
    /// <summary>Most records returned.</summary>
    public int Limit { get; init; } = 512;
}

/// <summary>One hop through one owned link from a set of live records.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleExpandQuery
{
    /// <summary>The module that owns the link.</summary>
    public required string ModuleId { get; init; }
    /// <summary>The owned relationship name.</summary>
    public required string Relation { get; init; }
    /// <summary>Out: the ids are the link's sources. In: the ids are records of its target type, and the hop finds their sources.</summary>
    public required ModuleTraversalDirection Direction { get; init; }
    /// <summary>Live version ids of the starting records.</summary>
    public required IReadOnlyList<string> FromIds { get; init; }
    /// <summary>Most records one starting record leads to.</summary>
    public int FanOut { get; init; } = 8;
    /// <summary>Most question words one expansion ranks by.</summary>
    public const int MaximumWords = 16;
    /// <summary>
    /// Most neighbours of one starting record the store reads and ranks (red-team M3): beyond it, in id order, a hub's other
    /// neighbours are not read at all, so its degree no longer sets a hop's cost. Far above any fan-out, so ranking is unchanged
    /// for every record with fewer neighbours.
    /// </summary>
    public const int MaximumScan = 1024;
    /// <summary>
    /// The question's words, lower case (CMP-4). When a starting record leads to more than the fan-out, the reader keeps first the
    /// records whose text (<see cref="Modules.ModuleType.TextProperties"/>) holds most of them, then the most specific: those
    /// fewest records share the link with. Empty keeps the most specific first.
    /// </summary>
    public IReadOnlyList<string> Words { get; init; } = [];
}

/// <summary>What one hop reached.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleTraversalStep
{
    /// <summary>Read disposition; refusal reaches nothing.</summary>
    public required ModuleReadStatus Status { get; init; }
    /// <summary>Each reached record with the starting record that led to it.</summary>
    public IReadOnlyList<ModuleTraversalEdge> Edges { get; init; } = [];
    /// <summary>Refusal details.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];
}

/// <summary>A reached live record and the record it was reached from.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleTraversalEdge
{
    /// <summary>The live version id of the starting record.</summary>
    public required string FromId { get; init; }
    /// <summary>The reached live record.</summary>
    public required ModuleRecord To { get; init; }
    /// <summary>
    /// How many records share this link with the reached one: for an incoming hop, how many records of this relation the reached
    /// record links to; for an outgoing hop, how many link to it. A test linked to one type says more about it than one linked to
    /// twelve. At least 1.
    /// </summary>
    public int Degree { get; init; } = 1;
}
