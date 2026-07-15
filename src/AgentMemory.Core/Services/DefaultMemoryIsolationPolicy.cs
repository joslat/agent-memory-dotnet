using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Core.Services;

/// <summary>
/// Default <see cref="IMemoryIsolationPolicy"/>: reproduces today's behavior exactly under
/// <see cref="MemoryIsolationMode.SingleTenant"/> (the default), adds a structured, content-free warning
/// under <see cref="MemoryIsolationMode.WarnOnUnscoped"/>, and fails closed under
/// <see cref="MemoryIsolationMode.StrictMultiTenant"/> — in every case only for
/// <see cref="MemoryOperationAccess.Tenant"/> operations that resolve to an unscoped/global result.
/// <see cref="MemoryOperationAccess.Administrative"/> operations are never warned or blocked: that access
/// level is itself the explicit, intentional path the issue asks for.
/// </summary>
internal sealed class DefaultMemoryIsolationPolicy : IMemoryIsolationPolicy
{
    private readonly IOptions<MemoryIsolationOptions> _options;
    private readonly ILogger<DefaultMemoryIsolationPolicy> _logger;

    public DefaultMemoryIsolationPolicy(IOptions<MemoryIsolationOptions> options, ILogger<DefaultMemoryIsolationPolicy> logger)
    {
        _options = options;
        _logger = logger;
    }

    public MemoryScope ResolveReadScope(
        MemoryScope? explicitScope,
        string? ownerId,
        string operationName,
        MemoryOperationAccess access)
    {
        var resolved = explicitScope ?? (string.IsNullOrEmpty(ownerId) ? MemoryScope.Global : MemoryScope.For(ownerId));
        Enforce(resolved.HasOwnerFilter, operationName, access, isWrite: false);
        return resolved;
    }

    public string? ResolveWriteOwner(
        string? ownerId,
        string operationName,
        MemoryOperationAccess access)
    {
        Enforce(!string.IsNullOrEmpty(ownerId), operationName, access, isWrite: true);
        return ownerId;
    }

    private void Enforce(bool hasOwner, string operationName, MemoryOperationAccess access, bool isWrite)
    {
        if (hasOwner || access == MemoryOperationAccess.Administrative)
            return;

        switch (_options.Value.Mode)
        {
            case MemoryIsolationMode.StrictMultiTenant:
                throw new MemoryOwnerScopeRequiredException(operationName);

            case MemoryIsolationMode.WarnOnUnscoped:
                _logger.LogWarning(
                    "Memory operation {Operation} ({Kind}) executed without an owner scope; isolation " +
                    "mode is {Mode}, falling back to {Behavior}.",
                    operationName, isWrite ? "write" : "read", MemoryIsolationMode.WarnOnUnscoped,
                    isWrite ? "shared" : "global");
                break;

            case MemoryIsolationMode.SingleTenant:
            default:
                break;
        }
    }
}
