namespace AgentMemory.Abstractions.Exceptions;

/// <summary>
/// Thrown by <see cref="AgentMemory.Abstractions.Services.IMemoryIsolationPolicy"/> when
/// <see cref="AgentMemory.Abstractions.Options.MemoryIsolationMode.StrictMultiTenant"/> is enabled and a
/// tenant-facing operation has no owner scope. Applications can catch this specific type rather than a
/// generic exception to detect a missing-owner-context bug at the point it happens, not after a global or
/// shared read/write has already occurred.
/// </summary>
public sealed class MemoryOwnerScopeRequiredException : MemoryException
{
    /// <summary>The name of the operation that required an owner scope.</summary>
    public string OperationName { get; }

    /// <summary>Initializes a new instance for the specified operation.</summary>
    /// <param name="operationName">The name of the operation that required an owner scope.</param>
    public MemoryOwnerScopeRequiredException(string operationName)
        : base(
            $"Memory operation '{operationName}' requires an owner scope " +
            "because strict multi-tenant isolation is enabled.")
    {
        OperationName = operationName;
    }
}
