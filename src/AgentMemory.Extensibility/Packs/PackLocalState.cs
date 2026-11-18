// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Ingestion;

namespace AgentMemory.Extensibility.Packs;

/// <summary>Host-bound local-copy identity; the store resolves owner policy again before IO.</summary>
[Experimental("AMEXT001")]
public sealed record PackLocalIdentity
{
    /// <summary>Host application/brain identity.</summary>
    public required string ApplicationId { get; init; }
    /// <summary>Resolved owner identity.</summary>
    public required string OwnerId { get; init; }
    /// <summary>The verified pack identity.</summary>
    public required string PackId { get; init; }
    /// <summary>The mounted verified content digest. Metadata-only Peek/Detach ignore this field; a host may pass an empty value there.</summary>
    public required string BaseDigest { get; init; }
}

/// <summary>A committed local-copy policy. Revisions prevent stale writes and lost settings updates.</summary>
[Experimental("AMEXT001")]
public sealed record PackLocalPolicy
{
    /// <summary>The concurrency revision.</summary>
    public required long Revision { get; init; }
    /// <summary>When true, rejects every knowledge writer.</summary>
    public required bool WriteProtected { get; init; }
    /// <summary>Automatic writes additionally require this permission.</summary>
    public required bool AutomaticLearning { get; init; }
    /// <summary>Whether the mount still participates in this brain.</summary>
    public required bool Active { get; init; }
    /// <summary>The verified base whose keys were validated.</summary>
    public required string BaseDigest { get; init; }
    /// <summary>The verified published version, when retained by this policy pin.</summary>
    public string BaseVersion { get; init; } = "";
}

/// <summary>A history-preserving local assertion; null proposal restores the published value.</summary>
[Experimental("AMEXT001")]
public sealed record PackLocalChange
{
    /// <summary>Version identity.</summary>
    public required string Id { get; init; }
    /// <summary>Declared type.</summary>
    public required string Type { get; init; }
    /// <summary>Logical key.</summary>
    public required string Key { get; init; }
    /// <summary>User/system provenance; a permission check, not a publisher claim.</summary>
    public required string Actor { get; init; }
    /// <summary>Creation instant.</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }
    /// <summary>Exclusive transaction-time end.</summary>
    public DateTimeOffset? InvalidatedAtUtc { get; init; }
    /// <summary>The locally asserted value, or a restore marker.</summary>
    public ModuleProposal? Proposal { get; init; }
}

/// <summary>The committed policy and selected local values, read in one transaction.</summary>
[Experimental("AMEXT001")]
public sealed record PackLocalSnapshot
{
    /// <summary>Current effective protection policy.</summary>
    public required PackLocalPolicy Policy { get; init; }
    /// <summary>At most one version for each type/key at the requested clock.</summary>
    public IReadOnlyList<PackLocalChange> Changes { get; init; } = [];
    /// <summary>Store-authoritative verified erasure tombstones, also applied to the retained published base.</summary>
    public IReadOnlyList<ModuleErasure> Erasures { get; init; } = [];
}

/// <summary>Content-free current mount status, suitable for scoped eligibility decisions.</summary>
[Experimental("AMEXT001")]
public sealed record PackLocalStatus
{
    /// <summary>The committed policy, including its revision.</summary>
    public required PackLocalPolicy Policy { get; init; }
    /// <summary>Whether current personal assertions exist. No record values or keys are returned.</summary>
    public required bool HasAssertions { get; init; }
}

/// <summary>Optional local-copy persistence; absent from the dark path.</summary>
[Experimental("AMEXT001")]
public interface IPackLocalStore
{
    /// <summary>Reads at most 32 policies/assertion-presence flags in one owner/brain scope, without loading content.</summary>
    Task<IReadOnlyDictionary<string, PackLocalStatus>> PeekManyAsync(IReadOnlyList<PackLocalIdentity> identities, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This store does not expose bounded scoped status batches.");
    /// <summary>Reads owner/brain-scoped policy metadata without requiring a loaded, readable or unexpired base; null means no mount.</summary>
    Task<PackLocalPolicy?> PeekAsync(PackLocalIdentity identity, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This store does not expose scoped mount metadata.");
    /// <summary>Restrictively deactivates the current scoped pin under revision lock, without mounting/rebasing or loading its content.</summary>
    Task<PackLocalPolicy> DetachAsync(PackLocalIdentity identity, long expectedRevision, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This store does not support metadata-only detach.");
    /// <summary>Creates a protected-by-default mount, or returns its existing captured policy.</summary>
    Task<PackLocalPolicy> MountAsync(PackLocalIdentity identity, bool writeProtected = true, CancellationToken cancellationToken = default);
    /// <summary>Reads this owner's local values; a historical read never substitutes current amendments.</summary>
    Task<PackLocalSnapshot> ReadAsync(PackLocalIdentity identity, DateTimeOffset? systemAsOfUtc = null, CancellationToken cancellationToken = default);
    /// <summary>Updates protection under the same transaction lock used by every writer.</summary>
    Task<PackLocalPolicy> ConfigureAsync(PackLocalIdentity identity, long expectedRevision, bool writeProtected, bool automaticLearning, bool active = true, CancellationToken cancellationToken = default);
    /// <summary>Writes a validated full assertion after protection, ownership, digest and revision checks.</summary>
    Task WriteAsync(PackLocalIdentity identity, long expectedRevision, ModuleProposal proposal, bool automatic = false, CancellationToken cancellationToken = default);
    /// <summary>Restores the published value without deleting local history.</summary>
    Task RestoreAsync(PackLocalIdentity identity, long expectedRevision, string type, string key, CancellationToken cancellationToken = default);
    /// <summary>History for one scoped key, newest first.</summary>
    Task<IReadOnlyList<PackLocalChange>> HistoryAsync(PackLocalIdentity identity, string type, string key, CancellationToken cancellationToken = default);
}
