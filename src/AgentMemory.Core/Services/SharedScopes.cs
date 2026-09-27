using AgentMemory.Abstractions.Options;

namespace AgentMemory.Core.Services;

/// <summary>
/// G-15: the shared-only scope, defined once. Shared memory has no owner; an owner-scoped read with shared
/// included reads the owner's rows and the owner-less ones, so reading as an owner id no row can hold (a control
/// character is never part of a user id) reads exactly the shared rows.
/// </summary>
/// <remarks>
/// Used wherever an operation without an owner must not reach any owner's memory: a shared write's entity
/// resolution, its supersession candidates and its derived-memory group reads. In a single-tenant store every
/// row is owner-less, so shared-only is everything there and nothing changes; in a multi-tenant store it keeps a
/// shared write from reading, superseding or aggregating tenants' private memories. Never written: a scope that
/// is <see cref="IsSharedOnly"/> stamps no owner.
/// </remarks>
internal static class SharedScopes
{
    /// <summary>The owner id no memory holds.</summary>
    internal const string SentinelOwner = "\u0001shared";

    /// <summary>Shared rows only.</summary>
    internal static MemoryScope SharedOnly { get; } = MemoryScope.For(SentinelOwner, includeShared: true);

    /// <summary>Whether <paramref name="scope"/> is the shared-only scope (its owner must never be written).</summary>
    internal static bool IsSharedOnly(MemoryScope? scope) => scope?.OwnerId == SentinelOwner;

    /// <summary>The owner a write under <paramref name="scope"/> gets: none for shared-only.</summary>
    internal static string? WriteOwner(MemoryScope? scope) => IsSharedOnly(scope) ? null : scope?.OwnerId;

    /// <summary>What an operation without an owner may read: shared rows only; with one, its own rows.</summary>
    internal static MemoryScope OwnedOrShared(string? ownerId) =>
        string.IsNullOrEmpty(ownerId) ? SharedOnly : MemoryScope.For(ownerId, includeShared: false);
}
