// SPDX-License-Identifier: Apache-2.0
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
    /// <summary>How traversal context reached the record, from the turn's anchor (CMP-1); null for an item a view found.</summary>
    public string? Via { get; init; }
    /// <summary>The first relation of that path, <c>NAME:out</c> or <c>NAME:in</c> (feature 11): the part of the pack that offered it.</summary>
    public string? ViaRelation { get; init; }
    /// <summary>
    /// Whether the hop that reached the record led to an earlier layer of its family (<see cref="TraversalMeaning.Why"/>: nearer
    /// intent) or a later one (<see cref="TraversalMeaning.What"/>: nearer code), by the layer order the family declares (CO-2b);
    /// null within one layer, for an anchor's own record, for an item a view found, or without a layer order. Data beside the
    /// reason: <see cref="Via"/> does not change.
    /// </summary>
    public TraversalMeaning? ViaMeaning { get; init; }
    /// <summary>The record's effective confidence in millionths (feature 09): capped by its source and signer; null when nothing caps it.</summary>
    public int? ConfidenceMicro { get; init; }
    /// <summary>The original source session.</summary>
    public required string SourceSessionId { get; init; }
    /// <summary>Copied source message identities; no excerpt or owner authority is implied.</summary>
    public IReadOnlyList<string> SourceMessageIds { get; init; } = [];
    /// <summary>For a knowledge pack record, the pack, version, digest and seed line it came from; null otherwise.</summary>
    public Ingestion.PackSource? SourcePack { get; init; }
}

/// <summary>
/// Which way a walk's hop went through a family's layers (CO-2b), for meaning only: the layer order says what a pack's records
/// mean to the others' (intent, then decisions, architecture, work and code), never which may link to which.
/// </summary>
[Experimental("AMEXT001")]
public enum TraversalMeaning
{
    /// <summary>To an earlier layer: a reason for what the hop started from (a requirement a test validates).</summary>
    Why = 1,
    /// <summary>To a later layer: what serves what the hop started from (the tests that validate a requirement).</summary>
    What = 2,
}
