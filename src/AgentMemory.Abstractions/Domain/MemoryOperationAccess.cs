namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// Classifies an operation for <see cref="AgentMemory.Abstractions.Services.IMemoryIsolationPolicy"/>
/// purposes. A null/absent owner is never, by itself, proof of administrative intent — callers must pass
/// <see cref="Administrative"/> explicitly.
/// </summary>
public enum MemoryOperationAccess
{
    /// <summary>A tenant-facing operation. Strict isolation mode fails closed when no owner is present.</summary>
    Tenant,

    /// <summary>An explicit, intentional cross-owner administrative operation. Never subject to strict-mode failure or the warn-mode log.</summary>
    Administrative
}
