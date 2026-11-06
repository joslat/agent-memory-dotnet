// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace AgentMemory.Extensibility.Ingestion;

/// <summary>Non-destructive operation requested for a module-owned key.</summary>
[Experimental("AMEXT001")]
public enum ProposalKind
{
    /// <summary>Create or update a current version; identical semantic state is a no-op.</summary>
    Assert,
    /// <summary>Invalidate a current version without deleting its history.</summary>
    Invalidate
}

/// <summary>How to identify a declared relation target. No fuzzy name matching is permitted.</summary>
[Experimental("AMEXT001")]
public enum LinkTargetKind
{
    /// <summary>Core entity ID, or logical key for the module type named by the relation.</summary>
    Identity,
    /// <summary>Exact name or alias of a core:Entity; storage must refuse ambiguity.</summary>
    EntityName
}

/// <summary>Serializable requested state; owner and module identity come from the host/provider, never this object.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleProposal
{
    /// <summary>Declared type owned by the provider's module.</summary>
    public required string Type { get; init; }
    /// <summary>Nonempty stable logical key, without control characters.</summary>
    public required string Key { get; init; }
    /// <summary>Declared scalar values. Omit absent optional properties; explicit null is not stored.</summary>
    public IReadOnlyDictionary<string, JsonElement> Properties { get; init; } =
        new Dictionary<string, JsonElement>().AsReadOnly();
    /// <summary>Requested outgoing links; storage applies these to the prior version's links.</summary>
    public IReadOnlyList<ProposedLink> Links { get; init; } = [];
    /// <summary>Source IDs and copied excerpt retained after session clearing.</summary>
    public required ProposalSource Source { get; init; }
    /// <summary>Assert by default. Invalidate carries no properties or links.</summary>
    public ProposalKind Kind { get; init; }
    /// <summary>
    /// Logical keys of records of the same type that this new record replaces (an Assert only; never its own key, which is an
    /// ordinary new version). A replacement is a judged closing: the host's closing approver decides each one when the batch is
    /// written, and only records the provider was shown in <see cref="IngestionRequest.Existing"/> can be closed. An approved
    /// closing invalidates the replaced record non-destructively, in the same transaction as this record.
    /// </summary>
    public IReadOnlyList<string> Replaces { get; init; } = [];
    /// <summary>A pack record's life (feature 07): when it is true and when a host stops using it; null for a record without one.</summary>
    public Reading.ModuleRecordLife? Life { get; init; }
    /// <summary>The keys of records of its type, in the same pack, that this pack record supersedes (feature 07): resolved at read.</summary>
    public IReadOnlyList<string> Supersedes { get; init; } = [];
    /// <summary>
    /// How far the record is trusted, in millionths (feature 09): its own on a record line; once a host loads its pack, the least of
    /// its own, its source input's and its signer's. Null when nothing caps it.
    /// </summary>
    public int? ConfidenceMicro { get; init; }
    /// <summary>The input of the pack's derivation the record came from (feature 09): a name in <c>META/derivation.json</c>.</summary>
    public string? SourceKey { get; init; }
}

/// <summary>Outgoing relationship; its target type is fixed by the declaring module's ontology.</summary>
[Experimental("AMEXT001")]
public sealed record ProposedLink
{
    /// <summary>Declared relationship name for the source type.</summary>
    public required string Relation { get; init; }
    /// <summary>Target key, entity ID, or exact entity name/alias according to TargetKind.</summary>
    public required string Target { get; init; }
    /// <summary>Identity by default; names are permitted only for core entities.</summary>
    public LinkTargetKind TargetKind { get; init; }
}

/// <summary>Bounded, untrusted provenance copied into a stored version after membership verification.</summary>
[Experimental("AMEXT001")]
public sealed record ProposalSource
{
    /// <summary>Distinct IDs from the request; store membership must still be verified.</summary>
    public IReadOnlyList<string> MessageIds { get; init; } = [];
    /// <summary>Nonempty copied excerpt of at most 500 UTF-16 code units; it is not a trusted instruction.</summary>
    public required string Excerpt { get; init; }
    /// <summary>
    /// For a knowledge pack record, the pack it came from (Extensibility 0.13 §K2); null for a record a turn wrote. Only the
    /// pack importer writes it, and such a record cites no messages.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PackSource? Pack { get; init; }
}

/// <summary>Where a knowledge pack record came from: the pack, its version and bundle digest, and the seed line.</summary>
[Experimental("AMEXT001")]
public sealed record PackSource
{
    /// <summary>The pack id.</summary>
    public required string Id { get; init; }
    /// <summary>The pack version that wrote this record version.</summary>
    public required string Version { get; init; }
    /// <summary>The signed bundle digest of that version.</summary>
    public required string Digest { get; init; }
    /// <summary>The seed file.</summary>
    public required string File { get; init; }
    /// <summary>The 1-based line in the seed file.</summary>
    public required int Line { get; init; }
    /// <summary>
    /// For a snapshot's record that arrived in a signed snapshot pack, that pack (PACK-bind): the record belongs to the
    /// definition named by <see cref="Id"/>, but who signed it, and in which exact pack, stays with it. Null otherwise.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PackCarrier? Carrier { get; init; }
    /// <summary>
    /// The usage policy that governs this record (feature 10, R-CORE-139): <c>sha256:</c> over the exact bytes of the
    /// <c>policy.json</c> of the pack that carried it, or null when that pack has none. A host offers the record only while it
    /// holds that policy and the policy allows it, so a record whose policy the host has not loaded is withheld.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? UsagePolicy { get; init; }
}

/// <summary>The signed pack that carried a snapshot's records (pack format v1.2 §3): its id, version, digest and signers.</summary>
[Experimental("AMEXT001")]
public sealed record PackCarrier
{
    /// <summary>The snapshot pack's id.</summary>
    public required string Id { get; init; }
    /// <summary>Its version.</summary>
    public required string Version { get; init; }
    /// <summary>Its digest: <c>sha256:</c> over the exact bytes of its file list.</summary>
    public required string Digest { get; init; }
    /// <summary>Who signed it, as the loader verified it: role and key id, comma separated.</summary>
    public required string Signer { get; init; }
    /// <summary>The digest of its usage policy (feature 10), which governs the records it carried; null when it has none.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? UsagePolicy { get; init; }
}
