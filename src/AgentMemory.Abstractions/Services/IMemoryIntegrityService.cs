using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// G6 (PLAN 40.50): checks the store against rules every write is meant to keep: no edge between two owners' memories,
/// every closed-by-change fact has its successor, no live fact has one, every validity window ends after it begins, every
/// fact has a source. Read-only; the CLI verb is <c>agentmemory integrity</c>.
/// </summary>
public interface IMemoryIntegrityService
{
    /// <summary>Checks <paramref name="ownerId"/>'s memories, or the whole store when null.</summary>
    Task<MemoryIntegrityReport> CheckAsync(string? ownerId = null, CancellationToken cancellationToken = default);
}
