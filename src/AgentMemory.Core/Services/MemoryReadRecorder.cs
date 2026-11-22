using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Services;

/// <summary>One recall's worth of read items, with the time of the read (DRT-5).</summary>
/// <param name="Nodes">The items the recall returned.</param>
/// <param name="ReadAtUtc">When the recall handed them to the queue, not when they are written.</param>
internal readonly record struct MemoryRead(IReadOnlyList<(string NodeId, MemoryNodeKind Kind)> Nodes, DateTimeOffset ReadAtUtc);

/// <summary>
/// DRT-5 (decision D, steps 1 and 2): a decay service that can write the read notes of many recalls in one transaction,
/// each with its own read time. The access-tracking queue uses it when the registered decay service has it, and
/// otherwise writes each recall with <see cref="Abstractions.Services.IMemoryDecayService.UpdateAccessTimestampsAsync"/>,
/// as before.
/// </summary>
/// <remarks>
/// The stored state is that of one <c>UpdateAccessTimestampsAsync</c> per read, in the order given, each stamped with
/// its read's time: within a read an item counts once; across reads it counts once per read, with one audit row each.
/// Internal, so the public decay interface and its implementers do not change.
/// </remarks>
internal interface IMemoryReadRecorder
{
    /// <summary>
    /// Writes the reads in one transaction, and returns how many were stored. When that transaction fails for any reason
    /// but an unreachable store, each read is written on its own, once, and those that fail again are left out of the
    /// count. An unreachable store throws: retrying each read would hold the queue for the driver's retry time each.
    /// </summary>
    Task<int> RecordReadsAsync(IReadOnlyList<MemoryRead> reads, CancellationToken cancellationToken = default);
}
