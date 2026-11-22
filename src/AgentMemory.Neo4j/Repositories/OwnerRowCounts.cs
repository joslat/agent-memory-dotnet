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
/// <remarks>
/// DRT-PAUSE: a person who pauses longer than <see cref="Ttl"/> between turns made every recall count before its fact
/// search, one transaction in a row on recall's longest branch (4 round trips; with the store 20 ms away the recall
/// after a pause waited 13 round trips where back-to-back ones waited 8). A count older than <see cref="Ttl"/> but
/// younger than <see cref="MaxStale"/> is now answered at once and counted again in the background, so the plan uses
/// a count at most one recall old; older than that, or inside a write unit, it is counted first, as before.
/// </remarks>
internal sealed class OwnerRowCounts
{
    /// <summary>How long a count is trusted.</summary>
    internal static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    /// <summary>DRT-PAUSE: until this age a count past <see cref="Ttl"/> is answered while it is counted again.</summary>
    internal static readonly TimeSpan MaxStale = TimeSpan.FromMinutes(10);

    /// <summary>
    /// DRT-PAUSE: the AppContext switch <c>AgentMemory.Recall.OwnerCountsInLine</c> counts past <see cref="Ttl"/> before
    /// the search, as before. Read per call.
    /// </summary>
    internal static bool CountInLine => AppContext.TryGetSwitch("AgentMemory.Recall.OwnerCountsInLine", out var inLine) && inLine;

    /// <summary>Entries kept before the cache starts over (a bound, not a tuning knob).</summary>
    internal const int Capacity = 10_000;

    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset At)> _counts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _refreshing = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly IMemoryStoreContext? _store;

    public OwnerRowCounts(IMemoryStoreContext? store = null) : this(TimeProvider.System, store) { }

    internal OwnerRowCounts(TimeProvider time, IMemoryStoreContext? store = null)
    {
        _time = time;
        _store = store;
    }

    /// <summary>The cached count for <paramref name="key"/> in the current store, or <paramref name="count"/> run and cached.</summary>
    /// <param name="key">The owner (or shared corpus) and label.</param>
    /// <param name="count">The capped count, against the store.</param>
    /// <param name="refreshInBackground">
    /// Whether a count past <see cref="Ttl"/> may be refreshed in the background (DRT-PAUSE): only outside a write unit,
    /// whose ambient transaction a background read must not inherit.
    /// </param>
    public async Task<int> GetAsync(string key, Func<Task<int>> count, bool refreshInBackground = false)
    {
        var scoped = $"{_store?.ApplicationId}|{key}";
        var now = _time.GetUtcNow();
        if (_counts.TryGetValue(scoped, out var cached))
        {
            var age = now - cached.At;
            if (age < Ttl) return cached.Count;
            if (refreshInBackground && age < MaxStale)
            {
                Refresh(scoped, count);
                return cached.Count;
            }
        }
        var fresh = await count().ConfigureAwait(false);
        Remember(scoped, fresh, now);
        return fresh;
    }

    /// <summary>The background refresh in flight for a key, for tests; a completed task when there is none.</summary>
    internal Task Refreshing(string key) =>
        _refreshing.TryGetValue($"{_store?.ApplicationId}|{key}", out var task) ? task : Task.CompletedTask;

    private void Remember(string scoped, int count, DateTimeOffset at)
    {
        if (_counts.Count >= Capacity) _counts.Clear();
        _counts[scoped] = (count, at);
    }

    /// <summary>
    /// Counts again in the background, once per key at a time. It runs in the caller's execution context, so the
    /// ambient store is the caller's. A failure keeps the cached count; past <see cref="MaxStale"/> it is counted first.
    /// </summary>
    private void Refresh(string scoped, Func<Task<int>> count)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_refreshing.TryAdd(scoped, started.Task)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                Remember(scoped, await count().ConfigureAwait(false), _time.GetUtcNow());
            }
            catch (Exception)
            {
                // Kept: the cached count is at most MaxStale old, and the plan it picks is correct either way.
            }
            finally
            {
                _refreshing.TryRemove(scoped, out _);
                started.TrySetResult();
            }
        });
    }
}
