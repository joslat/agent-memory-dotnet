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
}
