using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// G5 (PLAN 40.49): why a memory was not recalled. Off the hot path: it runs the recall again and reads the memory, so it
/// costs a recall and a few reads; normal recall is unchanged.
/// </summary>
public interface IMemoryRecallExplainer
{
    /// <summary>For <paramref name="request"/>, why the fact <paramref name="factId"/> was or was not recalled.</summary>
    Task<MemoryWhyNot> WhyNotFactAsync(RecallRequest request, string factId, CancellationToken cancellationToken = default);
}
