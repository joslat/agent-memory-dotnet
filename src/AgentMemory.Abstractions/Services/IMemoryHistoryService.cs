using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Reads long-term memory lifecycle/history records across facts, entities, and preferences.
/// </summary>
public interface IMemoryHistoryService
{
    /// <summary>
    /// Returns normalized history rows ordered by most recent lifecycle activity first.
    /// </summary>
    Task<IReadOnlyList<MemoryHistoryRecord>> GetHistoryAsync(
        MemoryHistoryQuery query,
        CancellationToken cancellationToken = default);
}
