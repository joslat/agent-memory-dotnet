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
/// <b><c>Track</c> returns void (both overloads), and that is the contract, not an oversight.</b> A
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

    /// <summary>
    /// DRT-5b: <see cref="Track(IReadOnlyList{ValueTuple{string, MemoryNodeKind}})"/> with the time of the read, the recall's
    /// <c>MemoryContext.AssembledAtUtc</c>, so the stored read time is the context's whichever way it is written. The default
    /// ignores the time, for a tracker that stamps its own.
    /// </summary>
    void Track(IReadOnlyList<(string NodeId, MemoryNodeKind Kind)> nodes, DateTimeOffset readAtUtc) => Track(nodes);

    /// <summary>
    /// Waits until everything tracked before the call has been written, or given up on (a failed write is logged, a
    /// batch dropped by a full queue is counted). DRT-5: for a host that must see the read records right after a
    /// recall, or that ends or freezes between requests (a function, a command-line tool, a test). Never on the recall
    /// path. The default does nothing, for a tracker that writes as it tracks.
    /// </summary>
    Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
