namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Ambient, per-scope memory-store selector — the storage/application tier of the
/// <c>store ⊃ owner ⊃ session</c> model. A null <see cref="ApplicationId"/> selects the default
/// store. See <c>docs/Memory_Review_and_Implementation_Plan.md</c> (R1b).
/// </summary>
public interface IMemoryStoreContext
{
    /// <summary>Application / memory-store id for the current scope. Null = the default store.</summary>
    string? ApplicationId { get; }
}
