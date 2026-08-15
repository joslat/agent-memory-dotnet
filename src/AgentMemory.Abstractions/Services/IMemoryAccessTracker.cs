using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Records that memories were recalled, without the recall path waiting for it (30.12).
/// </summary>
/// <remarks>
/// <para>
/// Access stamps feed decay and retention. Nothing in a returned context depends on them, so a caller
/// blocked on the write is blocked on nothing — at shipped defaults that was up to 25 write
/// transactions before the model was even invoked.
/// </para>
/// <para>
/// <b><see cref="Track"/> returns void, and that is the contract, not an oversight.</b> A
/// <c>Task</c>-returning version invites a caller to await it, which reinstates exactly the latency this
/// exists to remove — and awaiting it inside a request scope is what makes the older
/// fire-and-forget approach unsafe, because the scope can be disposed under an in-flight write. The
/// implementation is a singleton owned by the root container and must never throw.
/// </para>
/// </remarks>
public interface IMemoryAccessTracker
{
    /// <summary>Queues one recall's worth of accessed nodes. Never blocks, never throws.</summary>
    void Track(IReadOnlyList<(string NodeId, MemoryNodeKind Kind)> nodes);
}
