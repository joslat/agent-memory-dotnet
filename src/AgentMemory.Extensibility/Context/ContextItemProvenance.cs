using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Context;

/// <summary>Host-resolved metadata carried through collection and selection.</summary>
[Experimental("AMEXT001")]
public sealed record ContextModuleBinding
{
    /// <summary>The owning contributor's module.</summary>
    public required string ModuleId { get; init; }
    /// <summary>Frozen manifest version, not a stored record version.</summary>
    public required string ModuleVersion { get; init; }
    /// <summary>Frozen contributor identity.</summary>
    public required string ContributorId { get; init; }
    /// <summary>The declared section type actually returned.</summary>
    public required string SectionType { get; init; }
    /// <summary>Resolved declaration fingerprint; not an activated/configured/signed set identity.</summary>
    public required string MetadataFingerprint { get; init; }
}

/// <summary>Record-version and copied source references accompanying untrusted rendered text.</summary>
/// <remarks>Custom contributors can supply these claims. They do not authorize reads/writes or change scope.
/// A declared-view contributor snapshots them from the record returned by its scoped reader.</remarks>
[Experimental("AMEXT001")]
public sealed record ContextItemProvenance
{
    /// <summary>The record's owning module, which may differ from the contributing module for a dependency read.</summary>
    public required string RecordModuleId { get; init; }
    /// <summary>The declared stored type.</summary>
    public required string RecordType { get; init; }
    /// <summary>Complete stable version identity; no numeric suffix is inferred.</summary>
    public required string RecordVersionId { get; init; }
    /// <summary>The logical key shared by successive stored versions.</summary>
    public required string RecordKey { get; init; }
    /// <summary>Creation time of this stored version.</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }
    /// <summary>Exclusive stored transaction-time end, when present.</summary>
    public DateTimeOffset? InvalidatedAtUtc { get; init; }
    /// <summary>The original source session.</summary>
    public required string SourceSessionId { get; init; }
    /// <summary>Copied source message identities; no excerpt or owner authority is implied.</summary>
    public IReadOnlyList<string> SourceMessageIds { get; init; } = [];
}
