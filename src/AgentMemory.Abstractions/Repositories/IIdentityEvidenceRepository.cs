using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Repositories;

/// <summary>
/// Explicit host-only evidence registration, separate from model extraction and Entity.Attributes.
/// A null owner selects shared entities only. Assertions are additive and idempotent by content/source;
/// retiring evidence requires a separate reviewed operation.
/// </summary>
public interface IIdentityEvidenceRepository
{
    /// <summary>Whether this backend's owned evidence schema was explicitly enabled.</summary>
    bool IsEnabled { get; }

    /// <summary>Register exactly the configured composite-key shape; extra values do not change that shape.</summary>
    Task AssertKeyAsync(string entityId, VerifiedIdentityKey key, IReadOnlyList<string> identityProperties,
        string? ownerId = null, CancellationToken cancellationToken = default);

    /// <summary>Register an alias backed by an explicit host authority and source.</summary>
    Task AssertAliasAsync(string entityId, VerifiedIdentityAlias alias, string authority,
        string? ownerId = null, CancellationToken cancellationToken = default);

    /// <summary>Register semantic kind; conflicting active kinds or complete calendar values are rejected.</summary>
    Task AssertKindAsync(string entityId, VerifiedIdentityKind kind,
        string? ownerId = null, CancellationToken cancellationToken = default);

    /// <summary>Register the host's name-uniqueness namespace for this exact current primary name.</summary>
    Task AssertNameNamespaceAsync(string entityId, string nameNamespace, string authority, string sourceId,
        string? ownerId = null, CancellationToken cancellationToken = default);
}
