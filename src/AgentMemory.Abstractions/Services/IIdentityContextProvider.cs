using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Abstractions.Services;

/// <summary>Explicit application assertions, never inferred from extracted entity attributes.</summary>
public sealed record IdentityHostEvidence
{
    /// <summary>Host-backed composite keys; incomplete keys cannot establish identity.</summary>
    public IReadOnlyList<VerifiedIdentityKey> Keys { get; init; } = Array.Empty<VerifiedIdentityKey>();
    /// <summary>Optional host-backed semantic role.</summary>
    public VerifiedIdentityKind? Kind { get; init; }
    /// <summary>Optional host-backed namespace for name/alias identity.</summary>
    public VerifiedIdentityNameNamespace? NameNamespace { get; init; }
}

/// <summary>
/// Optional trusted application seam, called before candidate reads outside a write transaction.
/// Inputs are frozen untrusted observations and source snapshots. Implementations return only
/// assertions they can substantiate, never promote model attributes into verified evidence.
/// Disabled identity never resolves or calls this service. The caller's cancellation is passed through.
/// Provider failures and invalid assertions propagate before candidate reads; callers can retry
/// the unacknowledged ingestion. Returning null explicitly supplies no host assertions.
/// </summary>
public interface IIdentityContextProvider
{
    /// <summary>Supply explicit assertions for this mention/scope, or null when none is available.</summary>
    Task<IdentityHostEvidence?> GetContextAsync(ExtractedEntity mention, EntityIdentityResolutionContext sources,
        MemoryScope? scope = null, CancellationToken cancellationToken = default);
}
