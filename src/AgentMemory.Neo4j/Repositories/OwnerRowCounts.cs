using System.Collections.Concurrent;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Neo4j.Repositories;

/// <summary>
/// G-14: how many rows an owner (or the shared corpus) holds, remembered briefly, so deciding “small enough to
/// scan” costs one capped count every <see cref="Ttl"/> rather than one per recall. A count may be up to
/// <see cref="Ttl"/> old: an owner that just crossed the threshold is scanned a little longer, which is correct,
/// only slower. Keyed by the ambient store (an owner id small in one application's database may be large in
/// another's) and bounded. Singleton.
/// </summary>
internal sealed class OwnerRowCounts
{
    /// <summary>How long a count is trusted.</summary>
    internal static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    /// <summary>Entries kept before the cache starts over (a bound, not a tuning knob).</summary>
    internal const int Capacity = 10_000;

    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset At)> _counts = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly IMemoryStoreContext? _store;

    public OwnerRowCounts(IMemoryStoreContext? store = null) : this(TimeProvider.System, store) { }

    internal OwnerRowCounts(TimeProvider time, IMemoryStoreContext? store = null)
    {
        _time = time;
        _store = store;
    }

    /// <summary>The cached count for <paramref name="key"/> in the current store, or <paramref name="count"/> run and cached.</summary>
    public async Task<int> GetAsync(string key, Func<Task<int>> count)
    {
        var scoped = $"{_store?.ApplicationId}|{key}";
        var now = _time.GetUtcNow();
        if (_counts.TryGetValue(scoped, out var cached) && now - cached.At < Ttl) return cached.Count;
        var fresh = await count().ConfigureAwait(false);
        if (_counts.Count >= Capacity) _counts.Clear();
        _counts[scoped] = (fresh, now);
        return fresh;
    }
}
