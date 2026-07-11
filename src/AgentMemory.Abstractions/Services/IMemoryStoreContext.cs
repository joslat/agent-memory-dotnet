namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Ambient, per-scope memory-store selector — the storage/application tier of the
/// <c>store ⊃ owner ⊃ session</c> model. A null <see cref="ApplicationId"/> selects the default
/// store. See <c>docs/archive/Memory_Review_and_Implementation_Plan.md</c> (R1b).
/// </summary>
public interface IMemoryStoreContext
{
    /// <summary>Application / memory-store id for the current scope. Null = the default store.</summary>
    string? ApplicationId { get; }
}

/// <summary>
/// A <see cref="IMemoryStoreContext"/> whose <see cref="ApplicationId"/> can be set, so adapters
/// (e.g. the MAF providers) can route the store per scope from an inbound identifier. Mutating a
/// process-wide singleton is only safe for a single application per host; register a scoped
/// implementation to route per request.
/// </summary>
public interface IWritableMemoryStoreContext : IMemoryStoreContext
{
    /// <summary>Application / memory-store id for the current scope. Null = the default store.</summary>
    new string? ApplicationId { get; set; }
}
