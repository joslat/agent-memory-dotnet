using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Modules;

/// <summary>
/// How a module joins traversal context (coding packs CMP-1): which of its records the turn's anchors match, which of its links
/// a walk may follow, and how many of the records reached it offers. A host that passes no anchors pays nothing for it.
/// </summary>
/// <remarks>
/// A walk starts at the records whose anchor properties match the turn's anchors (the file or symbol being edited), follows
/// declared links for at most two hops, and offers what it reached to the same gate as every other candidate, each with the
/// path that reached it as its reason. Only links a module owns can be declared traversable here, so a module decides how
/// others may arrive at its records and never how its records are reached from elsewhere.
/// </remarks>
[Experimental("AMEXT001")]
public sealed record ModuleTraversal
{
    /// <summary>Most records this module offers to the gate for one turn, 1 to 16.</summary>
    public const int MaximumOffered = 16;
    /// <summary>Most links a walk follows from an anchor.</summary>
    public const int MaximumHops = 3;
    /// <summary>Most relations one module declares traversable, so one pack cannot multiply a walk's reads (red-team M3).</summary>
    public const int MaximumRelations = 16;
    /// <summary>Most anchor declarations one module makes.</summary>
    public const int MaximumAnchors = 16;

    /// <summary>Properties whose values anchors are matched against.</summary>
    public IReadOnlyList<ModuleTraversalAnchor> Anchors { get; init; } = [];
    /// <summary>Owned links a walk may follow, with direction and weight.</summary>
    public IReadOnlyList<ModuleTraversalRelation> Relations { get; init; } = [];
    /// <summary>Owned types a walk passes through but this module never offers, such as files.</summary>
    public IReadOnlyList<string> Hubs { get; init; } = [];
    /// <summary>Most records this module offers for one turn, highest score first.</summary>
    public int MaxItems { get; init; } = 4;
    /// <summary>
    /// How many links this module's walk follows from an anchor, 1 to 3. Two by default; a code map takes three to reach, from a
    /// changed type, the tests of the types that use it.
    /// </summary>
    public int Hops { get; init; } = 2;
}

/// <summary>An owned string property that anchors are matched against.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleTraversalAnchor
{
    /// <summary>The owned type.</summary>
    public required string Type { get; init; }
    /// <summary>A declared string property of that type. Glob and prefix values may hold several, split by whitespace, ',' or ';'.</summary>
    public required string Property { get; init; }
    /// <summary>How the property's value is compared with an anchor.</summary>
    public ModuleAnchorMatch Match { get; init; } = ModuleAnchorMatch.Exact;
    /// <summary>Which anchors it is compared with.</summary>
    public Context.ContextAnchorKind Kind { get; init; } = Context.ContextAnchorKind.Path;
}

/// <summary>How a record's anchor property is compared with a turn's anchor.</summary>
[Experimental("AMEXT001")]
public enum ModuleAnchorMatch
{
    /// <summary>The value equals the anchor (a file's path, a symbol's SCIP identity).</summary>
    Exact,
    /// <summary>The value is a path prefix of the anchor: a folder or component that contains it.</summary>
    Prefix,
    /// <summary>The value is one or more globs (<c>*</c> within a segment, <c>**</c> across segments) that match the anchor.</summary>
    Glob,
    /// <summary>The value is a symbol whose simple name, or full identity, equals the anchor.</summary>
    Name,
}

/// <summary>An owned link a walk may follow.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleTraversalRelation
{
    /// <summary>Most records one record may lead to through this link, 1 to 32.</summary>
    public const int MaximumFanOut = 32;

    /// <summary>The owned relationship name.</summary>
    public required string Name { get; init; }
    /// <summary>Out follows the link from its source; In finds the sources that point at a record.</summary>
    public ModuleTraversalDirection Direction { get; init; } = ModuleTraversalDirection.Out;
    /// <summary>How much of a record's score passes along this link, in millionths, 1 to 1,000,000; default 800,000.</summary>
    public long WeightMicro { get; init; } = PackRegistry.DefaultWeightMicro;
    /// <summary>Most records one record may lead to through this link.</summary>
    public int FanOut { get; init; } = 8;
}

/// <summary>Which way a walk follows a link.</summary>
[Experimental("AMEXT001")]
public enum ModuleTraversalDirection
{
    /// <summary>From the link's source to its target.</summary>
    Out,
    /// <summary>From the link's target back to its sources.</summary>
    In,
}
