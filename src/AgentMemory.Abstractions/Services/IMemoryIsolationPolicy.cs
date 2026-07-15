using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Centralizes owner-scope resolution so isolation-mode enforcement lives in one place rather than being
/// scattered across every service that touches a <see cref="MemoryScope"/> or owner id. See
/// <c>docs/getting-started.md</c> "Owner isolation" and <c>docs/security/threat-model.md</c> for the
/// properties this is meant to hold.
/// </summary>
public interface IMemoryIsolationPolicy
{
    /// <summary>
    /// Resolves the <see cref="MemoryScope"/> a read/query operation should actually use.
    /// <see cref="MemoryIsolationMode.SingleTenant"/> and <see cref="MemoryIsolationMode.WarnOnUnscoped"/>
    /// always return a usable scope (never throw). <see cref="MemoryIsolationMode.StrictMultiTenant"/>
    /// throws <see cref="AgentMemory.Abstractions.Exceptions.MemoryOwnerScopeRequiredException"/> instead
    /// of resolving to an unscoped/global result when <paramref name="access"/> is
    /// <see cref="MemoryOperationAccess.Tenant"/>.
    /// </summary>
    /// <param name="explicitScope">A scope already supplied by the caller (e.g. <c>RecallOptions.Scope</c>), if any. Wins over <paramref name="ownerId"/> when set.</param>
    /// <param name="ownerId">The owner id known for this request (e.g. from <c>RecallRequest.UserId</c> or ambient <see cref="IMemoryOwnerContext"/>), if any.</param>
    /// <param name="operationName">A short, stable name identifying the calling operation (e.g. <c>"RecallAsync"</c>) — used only for logging/exception messages, never for behavior.</param>
    /// <param name="access">Whether this is a normal tenant-facing operation or an explicit administrative one.</param>
    MemoryScope ResolveReadScope(
        MemoryScope? explicitScope,
        string? ownerId,
        string operationName,
        MemoryOperationAccess access);

    /// <summary>
    /// Resolves the owner id a write/persist operation should stamp onto the new record. Same mode
    /// semantics as <see cref="ResolveReadScope"/>: <see cref="MemoryIsolationMode.StrictMultiTenant"/>
    /// throws for a <see cref="MemoryOperationAccess.Tenant"/> write with no owner instead of silently
    /// creating shared memory.
    /// </summary>
    /// <param name="ownerId">The owner id known for this request, if any.</param>
    /// <param name="operationName">A short, stable name identifying the calling operation — logging/exception messages only.</param>
    /// <param name="access">Whether this is a normal tenant-facing operation or an explicit administrative one.</param>
    string? ResolveWriteOwner(
        string? ownerId,
        string operationName,
        MemoryOperationAccess access);
}
