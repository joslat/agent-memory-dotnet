using System.Collections.Concurrent;

namespace AgentMemory.Neo4j.Services;

/// <summary>
/// Remembers, per owner, that a rebuild on read just failed, so recall does not retry it on every turn.
/// A singleton: the working-memory service is scoped, and the memory must outlive one request.
/// </summary>
internal sealed class WorkingMemoryRebuildBackoff
{
    /// <summary>How long a failed rebuild is not retried on read.</summary>
    internal static readonly TimeSpan Delay = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _retryAfter = new(StringComparer.Ordinal);

    internal bool IsWaiting(string ownerId, DateTimeOffset now) =>
        _retryAfter.TryGetValue(ownerId, out var after) && now < after;

    internal void Failed(string ownerId, DateTimeOffset now) => _retryAfter[ownerId] = now + Delay;

    internal void Succeeded(string ownerId) => _retryAfter.TryRemove(ownerId, out _);
}
