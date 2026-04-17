namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Aggregate counts of SAME_AS relationships grouped by status.
/// </summary>
public sealed record DeduplicationStats(int PendingCount, int ConfirmedCount, int RejectedCount, int MergedCount);
