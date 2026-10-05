using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// G5 (PLAN 40.49): why a memory was not recalled. Off the hot path: it runs the recall again and reads the memory, so it
/// costs a recall and a few reads; normal recall is unchanged.
/// </summary>
public interface IMemoryRecallExplainer
{
    /// <summary>
    /// For <paramref name="request"/>, why the memory <paramref name="itemId"/> of <paramref name="kind"/> (a fact, an entity
    /// or a preference) was or was not recalled.
    /// </summary>
    Task<MemoryWhyNot> WhyNotAsync(RecallRequest request, MemoryItemKind kind, string itemId, CancellationToken cancellationToken = default);
}
