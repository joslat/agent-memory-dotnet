using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Services;

/// <summary>Capabilities that a backend explicitly guarantees for identity candidate generation.</summary>
public sealed record IdentityCandidateCapabilities
{
    /// <summary>Queries enforce their candidate limit before loading candidate context.</summary>
    public bool BoundedReads { get; init; }
    /// <summary>Verified composite keys have an exact lookup route.</summary>
    public bool VerifiedKeyLookup { get; init; }
    /// <summary>Complete calendar values have an exact normalised lookup route.</summary>
    public bool CalendarValueLookup { get; init; }
    /// <summary>Exact evidence lookups preserve OrdinalIgnoreCase completeness when requested.</summary>
    public bool CaseInsensitiveIdentityNameLookup { get; init; }
    /// <summary>The approximate route can return same-type entities observed in given context messages, bounded and scoped.</summary>
    public bool ContextRootedLookup { get; init; }
}

/// <summary>A bounded query. Null OwnerId means shared rows only, never an unscoped write lookup.</summary>
public sealed record IdentityCandidateQuery
{
    /// <summary>Original mention name.</summary>
    public required string Name { get; init; }
    /// <summary>Entity type; it is query data, not a dynamic Cypher identifier.</summary>
    public required string Type { get; init; }
    /// <summary>Additional entity-type labels admitted by this mention's ontology policy.</summary>
    public IReadOnlyList<string> CompatibleTypes { get; init; } = Array.Empty<string>();
    /// <summary>Whether lexical/fuzzy candidates are requested on the approximate route.</summary>
    public bool UseFuzzyCandidates { get; init; } = true;
    /// <summary>Exact authority-name comparison whose completeness this query requires.</summary>
    public StringComparison IdentityNameComparison { get; init; } = StringComparison.Ordinal;
    /// <summary>Owner whose rows may be read; null selects shared rows only.</summary>
    public string? OwnerId { get; init; }
    /// <summary>Whether an owned query may also read shared entities.</summary>
    public bool IncludeShared { get; init; }
    /// <summary>Strict maximum rows returned, before any context hydration.</summary>
    public required int Limit { get; init; }
    /// <summary>Host-verified keys, distinct from extractor attributes.</summary>
    public IReadOnlyList<VerifiedIdentityKey> VerifiedKeys { get; init; } = Array.Empty<VerifiedIdentityKey>();
    /// <summary>Configured composite-key components; extra host attributes do not change that key.</summary>
    public IReadOnlyList<string> IdentityProperties { get; init; } = Array.Empty<string>();
    /// <summary>Optional verified namespace for name uniqueness or backed aliases.</summary>
    public string? NameNamespace { get; init; }
    /// <summary>Intrinsic complete calendar key, for example gregorian:2026-12-12.</summary>
    public string? CalendarKey { get; init; }
    /// <summary>
    /// The read-only context turns of the current extraction window (never its own roots). When non-empty, the approximate
    /// route also returns same-type entities observed in them, resolved to their current identity and marked
    /// <see cref="AgentMemory.Abstractions.Domain.IdentityCandidateSignals.ContextRooted"/>. Empty unless the type policy opts in.
    /// </summary>
    public IReadOnlyList<string> ContextSourceMessageIds { get; init; } = Array.Empty<string>();
}

/// <summary>A bounded result and whether the exact identity lookup was complete.</summary>
public sealed record IdentityCandidateSet
{
    /// <summary>Rows returned; a provider exceeding the requested limit violates the contract.</summary>
    public IReadOnlyList<IdentityCandidate> Candidates { get; init; } = Array.Empty<IdentityCandidate>();
    /// <summary>
    /// True only when no exact identity match was hidden by a limit. An incomplete lookup cannot
    /// authorise identity or be repaired by asking a model to choose among the visible rows.
    /// </summary>
    public bool ExactIdentityLookupComplete { get; init; }
}

/// <summary>
/// Optional backend capability. Implementations must enforce owner/type/limit in their queries;
/// fetching all entities and applying Take in the caller does not satisfy this contract.
/// </summary>
public interface IIdentityCandidateProvider
{
    /// <summary>Capabilities guaranteed by this backend.</summary>
    IdentityCandidateCapabilities Capabilities { get; }

    /// <summary>Bounded exact key, calendar and name candidates; no embedding is required.</summary>
    Task<IdentityCandidateSet> FindExactCandidatesAsync(
        IdentityCandidateQuery query, CancellationToken cancellationToken = default);

    /// <summary>Bounded lexical/vector candidates within the remaining candidate budget.</summary>
    Task<IdentityCandidateSet> FindApproximateCandidatesAsync(
        IdentityCandidateQuery query, float[]? embedding, CancellationToken cancellationToken = default);
}
