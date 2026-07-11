namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Ambient, per-scope owner/user selector for memory operations that do not carry an explicit
/// <c>RecallRequest</c>/<c>ExtractionRequest</c> — notably the LLM-invokable memory tools built from
/// <see cref="IMemoryQueryFacade"/>, whose parameters are filled by the model and therefore must not
/// be trusted to carry a user id. A null <see cref="UserId"/> means shared/global (R1). See
/// <c>docs/archive/Memory_Review_and_Implementation_Plan.md</c> (IC8).
/// </summary>
public interface IMemoryOwnerContext
{
    /// <summary>Owner/user id for the current scope. Null = shared/global.</summary>
    string? UserId { get; }
}

/// <summary>
/// A writable <see cref="IMemoryOwnerContext"/> so adapters (e.g. the MAF providers) can set the
/// owner for the current request/agent-run flow. Implementations should be safe to register as a
/// singleton — back <see cref="UserId"/> with <see cref="System.Threading.AsyncLocal{T}"/> so
/// concurrent flows cannot bleed into one another.
/// </summary>
public interface IWritableMemoryOwnerContext : IMemoryOwnerContext
{
    /// <summary>Owner/user id for the current scope. Null = shared/global.</summary>
    new string? UserId { get; set; }
}
