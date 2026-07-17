using System.Collections.Concurrent;

namespace AgentMemory.Nams.Identity.Internal;

/// <summary>
/// A process-local, per-key async mutual-exclusion lock. Protects against duplicate NAMS conversation
/// creation from concurrent resolutions of the SAME identity within one process only -- no cross-process
/// guarantee (see <see cref="INamsConversationStateStore"/>'s own doc). Known, accepted limitation: entries
/// are never evicted, so a long-lived process accumulates one semaphore per distinct identity ever seen --
/// acceptable for what the engineering plan itself calls "a process-local optimization", not a hardened
/// production primitive.
/// </summary>
internal sealed class KeyedAsyncLock
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new();

    public async Task<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken)
    {
        var semaphore = _semaphores.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                semaphore.Release();
        }
    }
}
