namespace AgentMemory.Abstractions.Options;

/// <summary>
/// Configuration for <see cref="AgentMemory.Abstractions.Services.IMemoryIsolationPolicy"/>.
/// </summary>
public sealed class MemoryIsolationOptions
{
    /// <summary>The isolation mode. Defaults to <see cref="MemoryIsolationMode.SingleTenant"/> for backward compatibility.</summary>
    public MemoryIsolationMode Mode { get; set; } = MemoryIsolationMode.SingleTenant;
}
