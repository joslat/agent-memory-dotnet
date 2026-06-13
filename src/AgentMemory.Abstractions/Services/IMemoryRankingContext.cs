using AgentMemory.Abstractions.Options;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Ambient, per-request ranking override (D3). The context assembler resolves a per-recall
/// <see cref="MemoryRankingOptions"/> from <c>RecallOptions.Intent</c> and publishes it here for the
/// duration of the recall; the long-term repositories read it to re-weight their vector search for that
/// request only. Null <see cref="Current"/> ⇒ no override (use the configured ranking). Mirrors
/// <see cref="IMemoryOwnerContext"/> / the store context (AsyncLocal-backed, singleton-safe).
/// </summary>
public interface IMemoryRankingContext
{
    /// <summary>The ranking options to use for the current request, or null to use the configured ones.</summary>
    MemoryRankingOptions? Current { get; }
}

/// <summary>
/// A writable <see cref="IMemoryRankingContext"/> so the assembler can set the per-request ranking for
/// the current recall flow. Implementations should be safe to register as a singleton — back
/// <see cref="Current"/> with <see cref="System.Threading.AsyncLocal{T}"/> so concurrent recalls cannot
/// bleed into one another.
/// </summary>
public interface IWritableMemoryRankingContext : IMemoryRankingContext
{
    /// <summary>The ranking options to use for the current request, or null to use the configured ones.</summary>
    new MemoryRankingOptions? Current { get; set; }
}
